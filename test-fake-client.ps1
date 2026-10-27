# 用 FakeClient.exe（会自己最小化→再弹到前台）验证 LolFocusGuard
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class G {
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static string FgProc() {
    uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
  public static IntPtr TopWindowOf(int targetPid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (GetWindow(h, 4) != IntPtr.Zero) return true;
      uint pid; GetWindowThreadProcessId(h, out pid);
      if ((int)pid == targetPid) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@

$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$fakeLog = Join-Path $env:TEMP 'fakeclient.log'

Get-Process LolFocusGuard,FakeClient -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $guardLog,$fakeLog -ErrorAction SilentlyContinue

$fake = Start-Process (Join-Path $PSScriptRoot 'FakeClient.exe') -PassThru
Start-Sleep -Seconds 2
"FakeClient pid=$($fake.Id) 前台=$([G]::FgProc())"

$guard = Start-Process (Join-Path $PSScriptRoot 'LolFocusGuard.exe') -ArgumentList '--targets=FakeClient','--verbose','--no-click-allow' -PassThru
Start-Sleep -Seconds 2
"守卫 pid=$($guard.Id)，观察 10 秒..."

Start-Sleep -Seconds 10
"当前前台=$([G]::FgProc())"

$guardText = if (Test-Path $guardLog) { Get-Content $guardLog -Raw } else { '' }
$fakeText = if (Test-Path $fakeLog) { Get-Content $fakeLog -Raw } else { '' }
"=== FakeClient ==="; $fakeText
"=== Guard ==="; $guardText

$blockCount = ([regex]::Matches($guardText, '拦截 #')).Count
$popCount = ([regex]::Matches($fakeText, 'pop')).Count
$minimizedCount = ([regex]::Matches($guardText, '刚才最小化=是')).Count
$hwnd = [G]::TopWindowOf($fake.Id)
$fgNow = [G]::FgProc()
$iconicNow = if ($hwnd -ne [IntPtr]::Zero) { [G]::IsIconic($hwnd) } else { $false }
"假客户端弹了 $popCount 次，守卫拦了 $blockCount 次（其中识别为'从最小化弹出' $minimizedCount 次），当前前台=$fgNow 假窗口最小化=$iconicNow"

$pass = $true
$suppressed = ([regex]::Matches($guardText, '放弃拦截 60 秒')).Count
# 防拉锯阀：同一窗口 10 秒内被拦超过 5 次就放弃 60 秒，所以期望拦的是前 6 次
$expectedBlocks = [Math]::Min($popCount, 5)
if ($blockCount -lt $expectedBlocks) { "FAIL: 只拦了 $blockCount 次（期望至少 $expectedBlocks 次）"; $pass = $false }
else { "ok  : 拦了 $blockCount 次（假客户端一共弹了 $popCount 次）" }
if ($popCount -gt 6 -and $suppressed -lt 1) { 'FAIL: 弹了这么多次却没触发防拉锯阀（会跟它一直拉锯）'; $pass = $false }
elseif ($suppressed -ge 1) { "ok  : 触发防拉锯阀 $suppressed 次（拦 6 次后主动放弃 60 秒）" }
if ($minimizedCount -lt 1) { 'FAIL: 从没识别出"从最小化弹出"' ; $pass = $false } else { "ok  : 识别到从最小化弹出的情况 $minimizedCount 次" }
# 注：FakeClient 每 1.2 秒会自己再弹一次，所以"结束时前台是谁"没有意义——它就是个死缠烂打的假客户端。
# 真正要看的是：每次弹出都被拦（上面那条）+ 拦的时候会不会最小化回去（最小化那条）。

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $fake.Id -Force -ErrorAction SilentlyContinue
"`n结果: " + $(if ($pass) { 'PASS' } else { 'FAIL' })
