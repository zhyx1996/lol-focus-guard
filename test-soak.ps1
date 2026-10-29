# 长时间回归：把「用户的两种处境」反复跑一遍
#
#   A) 保护期已经过期的情况下，客户端自己弹到前台 —— 与此同时你点了它（你要把它叫出来用）
#      → 必须 0 次打回（允许它留在前台）
#   B) 保护期过期之后，客户端自己弹到前台、你没有任何输入
#      → 必须至少打回 1 次
#
# A 和 B 交替跑多轮，能同时盯住两个方向的退化：
#   · A 失败 = 「我刚点开又被压回去」复发（用户最痛的那个）
#   · B 失败 = 「它自己弹的时候没拦住」复发（本来要解决的那个）
#
# 用 SendInput 真点鼠标：低级鼠标钩子看得见 SendInput 造的事件，和真人点击同一条代码路径。
# 注入被 ACE 拦掉时脚本报 SKIP，不当 FAIL（否则会把环境问题算成守卫的错）。
param(
    [int]$Rounds = 6,
    [int]$ProtectionMs = 3200,   # 大于"同一个窗口 3 秒保护期"，保证每一轮 A 都是"保护期已过期"的场景
    [int]$RestoreDelayMs = 620   # PopTarget 轮询 200ms + 最小化 300ms + 余量
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class S2 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
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
  public static bool ClickWindow(IntPtr h) {
    // 用户开着全屏浏览器/TopMost 窗口，测试窗口得先顶到 TopMost 组最前面，否则点下去是点在浏览器上
    SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    System.Threading.Thread.Sleep(200);
    RECT r; if (!GetWindowRect(h, out r)) return false;
    int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
    if (!SetCursorPos(x, y)) return false;
    System.Threading.Thread.Sleep(80);
    INPUT[] d = new INPUT[1]; d[0].type = 0; d[0].mi.dwFlags = 0x0002;
    INPUT[] u = new INPUT[1]; u[0].type = 0; u[0].mi.dwFlags = 0x0004;
    SendInput(1, d, Marshal.SizeOf(typeof(INPUT)));
    System.Threading.Thread.Sleep(50);
    SendInput(1, u, Marshal.SizeOf(typeof(INPUT)));
    return true;
  }
  public static string FgProc() {
    uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
  }
}
'@

$root = $PSScriptRoot
$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$flag = Join-Path $env:TEMP 'poptarget.flag'

# 只杀本测试自己的进程（按 pid / 路径），绝不按进程名杀真客户端
Get-Process LolFocusGuard, PopTarget -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $guardLog, $flag -ErrorAction SilentlyContinue

$pt = Start-Process (Join-Path $root 'PopTarget.exe') -ArgumentList 'target' -PassThru
Start-Sleep -Seconds 2
$ptH = [S2]::FindByPid($pt.Id)
if ($ptH -eq [IntPtr]::Zero) { '跳过：测试窗口没起来'; '结果: SKIP'; exit 0 }

$guard = Start-Process (Join-Path $root 'LolFocusGuard.exe') -ArgumentList '--targets=PopTarget', '--verbose' -PassThru
Start-Sleep -Milliseconds 700
if ($guard.HasExited) {
    '跳过：守卫启动即退出（单实例互斥锁可能还被上一轮实例占着），本轮结论无效。'
    Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
    '结果: SKIP'; exit 0
}
Start-Sleep -Seconds 2

# 输入注入探测：ACE 会拦 SendInput / SetCursorPos，被拦就报 SKIP（不算 FAIL）
[void][S2]::SetCursorPos(400, 400)
Start-Sleep -Milliseconds 150
$cur = New-Object S2+POINT
[void][S2]::GetCursorPos([ref]$cur)
if ($cur.X -ne 400 -or $cur.Y -ne 400) {
    "注意：这台机器上输入注入被拦了（指针停在 $($cur.X),$($cur.Y)，SetCursorPos 无效）。"
    '      A 阶段没法测（"你点了它"必须真点），只做 B 阶段。'
    $skipped = $true
} else {
    $skipped = $false
}

$pass = $true
$roundsOk = 0
$roundsBad = 0

function Reset-Target {
    # 让目标窗口回到"可见、但不是前台"的状态，并清掉可能残留的 flag
    Remove-Item $flag -ErrorAction SilentlyContinue
    [void][S2]::ShowWindow($ptH, 9)    # SW_RESTORE
    Start-Sleep -Milliseconds 250
    [void][S2]::ShowWindow($ptH, 6)    # SW_MINIMIZE：先藏到后台，让 cycle 从最小化开始
    Start-Sleep -Milliseconds 250
}

# ---------- 取日志（异步写，最多滞后 200ms，所以每次动作后都等够 300ms 再读） ----------
function Read-LogSince([int]$from) {
    $now = Get-Content $guardLog -Raw -ErrorAction SilentlyContinue
    if ($null -eq $now) { return '' }
    return $now.Substring([Math]::Min($from, $now.Length))
}

# 防拉锯阀（5 次压不动 → 15 秒放弃拦截）会污染下一轮，等它过期再继续
function Wait-SuppressionExpired {
    for ($k = 0; $k -lt 40; $k++) {
        $txt = Get-Content $guardLog -Raw -ErrorAction SilentlyContinue
        if ($null -eq $txt) { return }
        $m = [regex]::Matches($txt, '\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})\] 提示：.*放弃拦截 15 秒')
        if ($m.Count -eq 0) { return }
        $ts = [datetime]::ParseExact($m[$m.Count - 1].Groups[1].Value, 'yyyy-MM-dd HH:mm:ss.fff', $null)
        $age = ([datetime]::Now - $ts).TotalMilliseconds
        if ($age -ge 16000) { return }
        Start-Sleep -Milliseconds ([int](16500 - $age))
        return
    }
}

if (-not $skipped) {
    "=== A) 保护期已过期时，你把客户端叫出来（共 $Rounds 轮）==="
    for ($i = 1; $i -le $Rounds; $i++) {
        Wait-SuppressionExpired
        # 等过"同一个窗口 3 秒保护期"，保证这一轮不是靠上一轮的余温放行的
        Start-Sleep -Milliseconds $ProtectionMs
        Reset-Target
        Start-Sleep -Milliseconds $ProtectionMs
        $beforeLen = (Read-LogSince 0).Length
        Set-Content -Path $flag -Value 'cycle'
        # 必须盯住"最小化 → 恢复"这个转换再点：如果它刚要最小化的时候我们就点，
        # 点会落在一个已经挪到 (-32000,-32000) 的窗口坐标上 → 实际点中的是别的程序
        # （上一版就是这么把点击点到 msedge 上的，结论全无意义）。
        $sawIconic = $false
        for ($t = 0; $t -lt 100; $t++) {
            Start-Sleep -Milliseconds 20
            if ([S2]::IsIconic($ptH)) { $sawIconic = $true; break }
        }
        if (-not $sawIconic) { "  第 $i 轮：没看到最小化（cycle 没生效），跳过"; continue }
        $restored = $false
        for ($t = 0; $t -lt 100; $t++) {
            Start-Sleep -Milliseconds 20
            if (-not [S2]::IsIconic($ptH)) { $restored = $true; break }
        }
        if (-not $restored) { "  第 $i 轮：窗口没恢复，跳过"; continue }
        $clicked = [S2]::ClickWindow($ptH)
        if (-not $clicked) { "  第 $i 轮：点击没发出去（注入失败）"; $skipped = $true; break }
        Start-Sleep -Milliseconds 900

        $part = Read-LogSince $beforeLen
        $linesA = @($part -split "`r?`n" | Where-Object { $_.Trim() })
        # 只断言"你点了它之后"：点击之前它先弹、被拦，是规则正确（那一刻确实没有输入证据）。
        # 用户真正痛的是"点了还被压回去"，所以从"放行原因: 你点了这个窗口"那一行往后数拦截。
        $allowIdx = -1
        for ($j = 0; $j -lt $linesA.Count; $j++) {
            if ($linesA[$j] -match '放行原因: 你点了这个窗口') { $allowIdx = $j }
        }
        $blocksBefore = ([regex]::Matches($part, '拦截 #\d+：PopTarget')).Count
        if ($allowIdx -lt 0) {
            "  第 $i 轮: 注意 —— 没测到『你点了这个窗口』（点击可能没落在窗口上或被系统吞了）"
            ($linesA | Where-Object { $_ -match '前台变化|放行原因|不放行|取消拦截' } | Select-Object -Last 3)
            continue
        }
        $afterClick = ($linesA[$allowIdx..($linesA.Count - 1)]) -join "`n"
        $blocksAfter = ([regex]::Matches($afterClick, '拦截 #\d+：PopTarget')).Count
        $iconicNow = [S2]::IsIconic($ptH)
        if ($blocksAfter -gt 0) {
            "  第 $i 轮: FAIL —— 你点了它之后又被压回去 $blocksAfter 次（这就是『得切好几遍才切得出来』）"
            ($linesA[$allowIdx..([Math]::Min($allowIdx + 4, $linesA.Count - 1))])
            $pass = $false
            $roundsBad++
        } elseif ($iconicNow) {
            "  第 $i 轮: FAIL —— 你点了它，但窗口最后还是被最小化了"
            $pass = $false
            $roundsBad++
        } else {
            "  第 $i 轮: ok（点击之后 $blocksAfter 次打回、窗口还在前台；点击前它有 $blocksBefore 次自弹被打回）"
            $roundsOk++
        }
    }
}
Start-Sleep -Milliseconds $ProtectionMs
$before = (Get-Content $guardLog -Raw -ErrorAction SilentlyContinue)
$beforeLen = if ($null -eq $before) { 0 } else { $before.Length }
Set-Content -Path $flag -Value 'cycle'
Start-Sleep -Milliseconds 1300
$now = Get-Content $guardLog -Raw -ErrorAction SilentlyContinue
$part = if ($null -eq $now) { '' } else { $now.Substring([Math]::Min($beforeLen, $now.Length)) }
$blocksB = ([regex]::Matches($part, '拦截 #\d+：PopTarget')).Count
if ($blocksB -ge 1) { 'ok  : 它自己弹的时候被拦了（' + $blocksB + ' 次）' }
else {
    ($part -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 6)
    'FAIL: 它自己弹的时候一次都没拦'
    $pass = $false
}

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
Remove-Item $flag -ErrorAction SilentlyContinue

"A 轮通过 $roundsOk/$Rounds，失败 $roundsBad 轮"
if (-not $pass) { '结果: FAIL'; exit 1 }
elseif ($skipped) { '结果: SKIP（输入注入被拦，只验证了 B 阶段）'; exit 0 }
elseif ($roundsOk -eq 0) {
    # 一轮都没真测到（点击全被系统吞了/窗口没抬起来）—— 不能算通过
    '结果: SKIP（A 阶段没有有效轮次，只有 B 阶段有结论）'; exit 0
}
else { '结果: PASS' }
