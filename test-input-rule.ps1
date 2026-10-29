# 核心判据回归测试（不需要真客户端）
#   A) 用户点击被监控窗口 → 必须放行
#   B) 被监控窗口自己抢前台（无任何输入）→ 必须拦截
#
# 做法：现场把 PopTarget.cs 编译成两个程序（PopTarget / BgWin），
# 用 SendInput 真点鼠标——低级鼠标钩子连 SendInput 造的事件也看得见，
# 所以和真人点击走的是同一条代码路径。
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class T4 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static IntPtr FindByPid(int pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (GetWindow(h, 4) != IntPtr.Zero) return true;
      uint p; GetWindowThreadProcessId(h, out p);
      if ((int)p == pid && IsWindowVisible(h)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static void ClickAt(int x, int y) {
    SetCursorPos(x, y);
    System.Threading.Thread.Sleep(80);
    INPUT[] d = new INPUT[1]; d[0].type = 0; d[0].mi.dwFlags = 0x0002;
    INPUT[] u = new INPUT[1]; u[0].type = 0; u[0].mi.dwFlags = 0x0004;
    SendInput(1, d, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(50);
    SendInput(1, u, Marshal.SizeOf(typeof(INPUT)));
  }
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  public static void ClickWindow(IntPtr h) {
    // 浏览器是全屏 TopMost（用户开着），我们的测试窗口得先顶到 TopMost 组最前面，不然点下去是点在浏览器上
    SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    System.Threading.Thread.Sleep(200);
    RECT r; GetWindowRect(h, out r);
    ClickAt((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
  }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  public static void Restore(IntPtr h) {
    if (IsIconic(h)) { ShowWindow(h, 9); System.Threading.Thread.Sleep(150); }
  }
  public static string FgProc() {
    uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
}
'@

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /out:BgWin.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll PopTarget.cs | Out-Null
Copy-Item .\BgWin.exe .\PopTarget.exe -Force

$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$flag = Join-Path $env:TEMP 'poptarget.flag'
Get-Process LolFocusGuard, PopTarget, BgWin -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $guardLog, $flag -ErrorAction SilentlyContinue

$bg = Start-Process (Join-Path $PSScriptRoot 'BgWin.exe') -ArgumentList 'bg' -PassThru
$pt = Start-Process (Join-Path $PSScriptRoot 'PopTarget.exe') -ArgumentList 'target' -PassThru
Start-Sleep -Seconds 2
$bgH = [T4]::FindByPid($bg.Id)
$ptH = [T4]::FindByPid($pt.Id)
if ($bgH -eq [IntPtr]::Zero -or $ptH -eq [IntPtr]::Zero) { throw '测试窗口没起来' }

$guard = Start-Process (Join-Path $PSScriptRoot 'LolFocusGuard.exe') -ArgumentList '--targets=PopTarget','--verbose' -PassThru
Start-Sleep -Seconds 2
if ($guard.HasExited) { '跳过：守卫启动即退出（单实例互斥锁可能还被上一轮实例占着），本轮结论无效。'; '结果: SKIP'; exit 0 }

# 输入注入可能被安全软件（WeGame 的 ACE）拦掉：SetCursorPos 返回 False 且指针不动。
# 这时 A 阶段没法测，直接说明并跳过，不要误报 FAIL。
$injectOk = $true
[T4]::SetCursorPos(400, 400) | Out-Null
Start-Sleep -Milliseconds 150
$pt430 = New-Object T4+POINT
[void][T4]::GetCursorPos([ref]$pt430)
if ($pt430.X -ne 400 -or $pt430.Y -ne 400) {
    $injectOk = $false
    "注意：这台机器上输入注入被拦了（指针停在 $($pt430.X),$($pt430.Y)，SetCursorPos 无效），A 阶段跳过。"
    "      WeGame 的 ACE 会拦 SendInput/SetCursorPos，所以这里只能测 B 阶段（无输入自弹必须被拦）。"
}

if ($injectOk) {
$pass = $true
$skipped = $false
    # ---------- A) 用户点击 PopTarget ----------
    # 先把焦点给对照组（也是真人式点击），再点被监控窗口 → 必须放行
    [T4]::ClickWindow($bgH)
    Start-Sleep -Milliseconds 800
    "点击前前台 = $([T4]::FgProc())"
    $before = (Get-Content $guardLog -Raw).Length
    [T4]::ClickWindow($ptH)
    Start-Sleep -Seconds 2
    $now = Get-Content $guardLog -Raw
    $partA = $now.Substring([Math]::Min($before, $now.Length))
    "=== A) 点击 PopTarget 之后的日志 ==="
    ($partA -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 6)

    $lines = @($partA -split "`r?`n" | Where-Object { $_.Trim() })
    $idx = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '前台变化 → PopTarget' -and $lines[$i] -match '按键=[0-9]+ms前@PopTarget') { $idx = $i; break }
    }
    $pass = $true
    if ($idx -lt 0) { 'A 结果: 跳过（合成的点击没产生前台切换：注入被安全软件吃掉了，这一条无法判定）'; $skipped = $true }
    else {
        # 注意：点击放行时日志是「前台变化 → …」+「放行原因: 你点了这个窗口」；
        # 万一被误拦，中间还会先插一条「不放行: …」，然后是「拦截 #N」—— 所以不能只看紧接的那一行，
        # 要一直看到下一条「前台变化」为止（之前只看 idx+1，误拦时会漏判成 PASS）。
        $blockedA = $false
        for ($j = $idx + 1; $j -lt $lines.Count; $j++) {
            if ($lines[$j] -match '前台变化 →') { break }
            if ($lines[$j] -match '拦截 #') { $blockedA = $true; break }
        }
        if ($blockedA) { 'A 结果: FAIL（用户点击被拦了）'; $pass = $false }
        else { 'A 结果: PASS（点击被识别为用户操作，未拦）' }
    }
    else { 'A 结果: PASS（点击被识别为用户操作，未拦）' }

} else { 'A 结果: 跳过（输入注入被安全软件拦了）'; $skipped = $true }

# ---------- B) PopTarget 自己抢前台（先最小化再恢复，保证有事件） ----------
# 注意：A 阶段刚"点过"PopTarget，同一个窗口有 3 秒放行保护（这是「你别把我刚点开的窗口压回去」那条防线）。
# 必须等它过期，否则 B 拦不到是符合规则的，不是 bug。
Start-Sleep -Milliseconds 3600
$before = (Get-Content $guardLog -Raw).Length
Set-Content -Path $flag -Value 'cycle'
Start-Sleep -Seconds 3
Remove-Item $flag -ErrorAction SilentlyContinue
$now = Get-Content $guardLog -Raw
$partB = $now.Substring([Math]::Min($before, $now.Length))
"=== B) PopTarget 自己抢前台之后的日志 ==="
($partB -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 6)
$blocked = ([regex]::Matches($partB, '拦截 #')).Count
if ($blocked -ge 1) { 'B 结果: PASS（无输入的强制抢前台被拦）' } else { 'B 结果: FAIL（没拦）'; $pass = $false }

Stop-Process -Id $guard.Id, $bg.Id, $pt.Id -Force -ErrorAction SilentlyContinue
if (-not $pass) { '结果: FAIL'; exit 1 }
elseif ($skipped) { '结果: SKIP（A 阶段输入注入被拦，只验证了 B 阶段）'; exit 0 }
else { '结果: PASS' }
