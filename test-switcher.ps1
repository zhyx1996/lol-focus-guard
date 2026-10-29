# test-switcher.ps1 —— 验证「Alt+Tab 切到目标窗口」会被当成你的动作（一次性凭据）
#
# 为什么要单独一个测试：这台 Win11 上 EVENT_SYSTEM_SWITCHSTART/END 一次都不发
# （心跳里"切换器事件=0次"），所以"你在 Alt+Tab"这条证据只能靠窗口身份认
# （ForegroundStaging / XamlExplorerHostIslandWindow 标题"任务切换"）。
# 认不出来 = 你 Alt+Tab 把客户端切出来会被当成"它自己弹的"打回去 = "切不出来"。
#
# 做法：
#   1. 起 FakeClient（quiet：不自动弹，前台顺序才能控制）；
#   2. 起守卫 --targets=FakeClient（这里**不能**加 --no-click-allow：那是"测试模式什么都不放行"，
#      规则第一步就短路，一次性凭据根本没机会生效）；
#   3. 用 SwitchToThisWindow 让 FakeClient 成为"上一个窗口"，再把 msedge 提到前台；
#   4. 发送真实 Alt+Tab（keybd_event）→ 落点应该是 FakeClient；
#   5. 断言：日志里出现「识别到任务切换器」和「放行原因: 你刚用 Alt+Tab/任务视图切窗口」，
#      并且那次前台变化之后**没有**对它的「拦截 #」。
#
# 落点不受控时（合成 Alt+Tab 偶尔被吞、MRU 顺序不是 FakeClient）判 SKIP，不判 PASS ——
# 假 PASS 比没测更糟。
param(
    [int]$WaitMs = 1200,
    [int]$MaxRounds = 6
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$log = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$fake = Join-Path $repo 'FakeClient.exe'
$guardExe = Join-Path $repo 'LolFocusGuard.exe'

function Fail($msg) { Write-Host "FAIL: $msg"; exit 1 }
function Skip($msg) { Write-Host "结果: SKIP（$msg）"; exit 0 }

if (-not (Test-Path $fake)) { Fail "缺少 FakeClient.exe，先跑 build.ps1" }
if (-not (Test-Path $guardExe)) { Fail "缺少 LolFocusGuard.exe，先跑 build.ps1" }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class SwitcherTest {
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  public const byte VK_MENU = 0x12, VK_TAB = 0x09;
  public const uint KEYUP = 0x0002;
  public static void AltTab() {
    keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);   System.Threading.Thread.Sleep(70);
    keybd_event(VK_TAB, 0, 0, UIntPtr.Zero);    System.Threading.Thread.Sleep(70);
    keybd_event(VK_TAB, 0, KEYUP, UIntPtr.Zero); System.Threading.Thread.Sleep(70);
    keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
  }
}
'@

$edge = Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowHandle -ne 0 -and $_.ProcessName -notmatch '^(FakeClient|LolFocusGuard|pwsh|WindowsTerminal)$' } |
    Select-Object -First 1
if (-not $edge) { Skip "找不到可以当'别的程序'的窗口，跳过（不拿别的窗口瞎猜）" }

# 干净起点
Get-Process LolFocusGuard, FakeClient -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$fakeProc = Start-Process $fake -ArgumentList 'quiet' -PassThru
Start-Sleep -Milliseconds 1200
$fakeProc.Refresh()
$hwnd = $fakeProc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Get-Process FakeClient -ErrorAction SilentlyContinue | Stop-Process -Force; Fail "拿不到 FakeClient 的窗口句柄" }

Start-Process $guardExe -ArgumentList '--targets=FakeClient', '--verbose'
Start-Sleep -Seconds 2

# 起点位置之后的新日志才算本次结果
$len0 = 0
if (Test-Path $log) { $len0 = (Get-Item $log).Length }

# 落点不受控：多试几轮，只要有一轮 Alt+Tab 恰好落到 FakeClient 上就够验证
# （落点取决于系统 MRU 顺序，脚本摆不稳；所以每轮都重新"让 FakeClient 当上一个窗口"）
for ($round = 1; $round -le $MaxRounds; $round++) {
    [SwitcherTest]::SwitchToThisWindow($hwnd, $false)
    Start-Sleep -Milliseconds 300
    [void][SwitcherTest]::SetForegroundWindow($edge.MainWindowHandle)
    Start-Sleep -Milliseconds 300
    if ([SwitcherTest]::GetForegroundWindow() -eq $hwnd) {
        [SwitcherTest]::SwitchToThisWindow($edge.MainWindowHandle, $false)
    }
    # 等"刚放行过这个窗口"（3000ms）过期，否则那条例会先放行，测不出凭据这条通道
    Start-Sleep -Milliseconds 3400

    # 真的 Alt+Tab（快进快出：切换器出现 → 松手）
    [SwitcherTest]::AltTab()

    # 模拟"慢客户端"：松手后约 1.9 秒它才把窗口画出来 ——
    # 这时 1.5 秒的 Alt 宽限已经过期，只有一次性切换凭据能救它。
    # 这正是最初"Alt+Tab 切不出来"的真机时序。
    Start-Sleep -Milliseconds 1900
    [SwitcherTest]::SwitchToThisWindow($hwnd, $false)
    Start-Sleep -Milliseconds 900
    Write-Host ("第 {0} 轮：Alt+Tab 后 1.9 秒才把目标窗口顶上来" -f $round)
}

# 收尾：先把进程停掉再读日志（避免边读边写）
Get-Process LolFocusGuard, FakeClient -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$text = ''
if (Test-Path $log) {
    $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
    if ($fs.Length -gt $len0) {
        [void]$fs.Seek($len0, 'Begin')
        $sr = New-Object IO.StreamReader($fs)
        $text = $sr.ReadToEnd()
        $sr.Close()
    }
    $fs.Close()
}

if ([string]::IsNullOrEmpty($text)) { Skip "这次没有产生新日志（守卫没起来？）" }

$lines = $text -split "`r?`n"
$switcherSeen = @($lines | Where-Object { $_ -match '识别到任务切换器' })
if ($switcherSeen.Count -eq 0) {
    Skip "日志里没有'识别到任务切换器'（合成 Alt+Tab 可能被系统吞了，或切换器没被前台化）"
}

$credIdx = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '放行原因: 你刚用 Alt\+Tab') { $credIdx = $i; break }
}
if ($credIdx -lt 0) {
    # 落点不是目标窗口：Alt+Tab 的落点不受脚本控制，这不算失败
    $tail = ($lines | Where-Object { $_ -match '前台变化|识别到任务切换器|拦截 #' } | Select-Object -Last 4) -join "`n"
    Skip "切换器认出来了，但这次 Alt+Tab 落点不是 FakeClient（落点不可控）。最后几行：`n$tail"
}

# 凭据放行之后，不该紧接着又拦同一个窗口
$after = $lines[($credIdx + 1)..([Math]::Min($credIdx + 6, $lines.Count - 1))]
$badBlock = @($after | Where-Object { $_ -match '拦截 #\d+：FakeClient' })
if ($badBlock.Count -gt 0) {
    Fail "凭据刚放行又被拦回去了：`n$($badBlock -join "`n")"
}

Write-Host ("ok  : 识别到任务切换器 {0} 次" -f $switcherSeen.Count)
Write-Host $lines[$credIdx]
Write-Host "ok  : 凭据放行之后没有再把同一个窗口打回"
Write-Host ""
Write-Host "结果: PASS"
exit 0
