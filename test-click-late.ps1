# 时序回归：**「促成这次前台变化的点击」比前台事件晚到时，不许误拦**。
#
# 症状来源：前台变化走 WinEvent 钩子，鼠标按下走低级鼠标钩子 / 15ms 采样线程。
# "点击 → 窗口被激活" 这条链上，前台事件完全可能比点击证据先到（ACE 掐掉鼠标钩子时，
# 最快也要等下一个 15ms 采样点）。判定那一刻看不到任何点击证据，就会被判成
# 「它自己弹的」→ 最小化，而实际上那是你自己点的。
#
# 守卫的对策（P0-B）：判定码是 BlockNoClick 的那一轮，动作线程先等 30ms 再复查，
# 这 30ms 内点击/Alt 证据到了就放弃这次拦截并写一行 "点击证据晚到"。
#
# 本脚本造这个时序：
#   A) 两条线程并行——一条立刻用 SetForegroundWindow 把 PopTarget 抬到前台，
#      另一条在 -ClickDelayMs（默认 10ms，远小于 30ms）之后用 SendInput 点一下。
#      断言：0 次拦截，且日志里有 "点击证据晚到"（P0-B 生效）或 "你点了这个窗口"
#      （点击证据抢在判定之前到达，同样合法）。
#   B) 对照组：只让 PopTarget 自己弹（无任何输入）→ 必须拦 ≥1 次。
#
# 为什么默认点 PopTarget 自己而不是任务栏：点任务栏会真的动用户的桌面（Win10 会弹开始菜单，
# Win11 会切换/预览用户开着的窗口），而 P0-B 的判据只看"有没有点击证据、在不在 350ms 内"，
# 不看落点。想跑任务栏那条路加 -ClickTarget taskbar（会点任务栏空白处，仍可能唤起开始菜单）。
param(
    # 点击比"抬前台"晚多少毫秒。"< 30ms" 才落在 P0-B 的复查窗口里；默认 10ms。
    [int]$ClickDelayMs = 10,
    # popup = 点 PopTarget 自己（默认，安全）；taskbar = 点任务栏空白处（更贴近真机，但会动桌面）
    [ValidateSet('popup', 'taskbar')]
    [string]$ClickTarget = 'popup'
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public class T5 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr FindWindow(string cls, string name);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool f);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }
  [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  public delegate bool EnumProc(IntPtr h, IntPtr l);

  public static IntPtr FindByPid(int pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      if (GetWindow(h, 4) != IntPtr.Zero) return true; // 只看顶层窗口
      uint p; GetWindowThreadProcessId(h, out p);
      if ((int)p == pid && IsWindowVisible(h)) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  public static string FgProc() {
    uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p);
    try { return System.Diagnostics.Process.GetProcessById((int)p).ProcessName; } catch { return "?"; }
  }

  // 立刻把窗口抬到前台（不等 150ms，供并行竞态用）
  public static bool RaiseFast(IntPtr h) {
    uint p;
    uint target = GetWindowThreadProcessId(h, out p);
    uint fg = GetWindowThreadProcessId(GetForegroundWindow(), out p);
    uint cur = GetCurrentThreadId();
    bool a1 = false, a2 = false;
    try {
      if (target != 0 && target != cur) a1 = AttachThreadInput(cur, target, true);
      if (fg != 0 && fg != cur) a2 = AttachThreadInput(cur, fg, true);
      ShowWindow(h, 9); // SW_RESTORE
      return SetForegroundWindow(h);
    } finally {
      if (a2) AttachThreadInput(cur, fg, false);
      if (a1) AttachThreadInput(cur, target, false);
    }
  }

  private static void Mouse(uint flag) {
    INPUT[] a = new INPUT[1];
    a[0].type = 0;              // INPUT_MOUSE
    a[0].mi.dwFlags = flag;     // 0x0002 = LEFTDOWN, 0x0004 = LEFTUP
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }

  // 在 (x,y) 上"按一下"：按下、保持 40ms、松开。
  // 40ms 的按住时长保证 15ms 采样线程一定能采到（钩子活着的话钩子会更快看到）。
  public static void ClickAt(int x, int y) {
    SetCursorPos(x, y);
    Mouse(0x0002);
    System.Threading.Thread.Sleep(40);
    Mouse(0x0004);
  }

  // 竞态：这条线程立刻抬前台，另一条线程在 delayMs 之后按一下。
  // 返回 true = 点击线程确实跑完了。
  public static bool RaceRaiseAndClick(IntPtr target, int x, int y, int delayMs) {
    bool clicked = false;
    System.Threading.Thread t = new System.Threading.Thread(delegate() {
      System.Threading.Thread.Sleep(delayMs);
      ClickAt(x, y);
      clicked = true;
    });
    t.IsBackground = true;
    t.Start();
    RaiseFast(target);              // 主线程立刻抬前台，和上面的点击并行
    t.Join();
    return clicked;
  }

  public static bool Center(IntPtr h, out int x, out int y) {
    RECT r;
    if (!GetWindowRect(h, out r)) { x = 0; y = 0; return false; }
    x = (r.Left + r.Right) / 2;
    y = (r.Top + r.Bottom) / 2;
    return true;
  }
}
'@

$repo = $PSScriptRoot
$guardExe = Join-Path $repo 'LolFocusGuard.exe'
$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$flag = Join-Path $env:TEMP 'poptarget.flag'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

# 现场编两个测试窗口（PopTarget.exe 当被监控窗口，BgWin.exe 当"原来在前台的那个"）
& $csc /nologo /target:winexe /optimize+ /out:BgWin.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll PopTarget.cs | Out-Null
if (-not (Test-Path (Join-Path $repo 'BgWin.exe'))) { throw '测试窗口编译失败' }
Copy-Item (Join-Path $repo 'BgWin.exe') (Join-Path $repo 'PopTarget.exe') -Force

# 只停"我们自己造出来的"进程：按可执行文件所在目录过滤，不按进程名批量杀
Get-Process LolFocusGuard, PopTarget, BgWin -ErrorAction SilentlyContinue |
    Where-Object {
        ($_.ProcessName -eq 'LolFocusGuard' -and $_.Path -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) -or
        ($_.Path -and $_.Path.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase))
    } | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item $guardLog, $flag -ErrorAction SilentlyContinue

# 记下用户的鼠标位置，结束时放回去（本脚本会移动指针）
$cursor0 = New-Object T5+POINT
[void][T5]::GetCursorPos([ref]$cursor0)

$bg = Start-Process (Join-Path $repo 'BgWin.exe') -ArgumentList 'bg' -PassThru
$pt = Start-Process (Join-Path $repo 'PopTarget.exe') -ArgumentList 'target' -PassThru
Start-Sleep -Seconds 2
$bgH = [T5]::FindByPid($bg.Id)
$ptH = [T5]::FindByPid($pt.Id)
if ($bgH -eq [IntPtr]::Zero -or $ptH -eq [IntPtr]::Zero) {
    Get-Process -Id $bg.Id, $pt.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    '结果: SKIP（测试窗口没起来）'
    exit 0
}

$guard = Start-Process $guardExe -ArgumentList '--targets=PopTarget', '--verbose' -PassThru
Start-Sleep -Seconds 2
if ($guard.HasExited) {
    Get-Process -Id $bg.Id, $pt.Id -ErrorAction SilentlyContinue | Stop-Process -Force
    '跳过：守卫启动即退出（单实例互斥锁可能还被上一轮实例占着），本轮结论无效。'
    '结果: SKIP'
    exit 0
}

function Read-Log { if (Test-Path $guardLog) { (Get-Content $guardLog -Raw) } else { '' } }

# 从最后一条心跳里取"点击采样 / 钩子点击"计数（这两个加起来就是"系统看见了几次按下"）
function Get-ClickCounters {
    param([string]$Text)
    $last = ($Text -split "`r?`n" | Where-Object { $_ -match '心跳' } | Select-Object -Last 1)
    if (-not $last) { return $null }
    $m = [regex]::Match($last, '点击采样=(\d+)/钩子=(\d+)')
    if (-not $m.Success) { return $null }
    return @{ Sample = [int]$m.Groups[1].Value; Hook = [int]$m.Groups[2].Value }
}

function Cleanup {
    Get-Process -Id $guard.Id, $pt.Id, $bg.Id -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item $flag -ErrorAction SilentlyContinue
    try { [void][T5]::SetCursorPos($cursor0.X, $cursor0.Y) } catch { }
}

$pass = $true
$skipped = $false

# ---------- 注入探测：这台机器上 SendInput 到底能不能被系统看见 ----------
# ACE 会拦 SendInput / SetCursorPos。先点一下自己的 BgWin，看心跳里的计数有没有涨：
# 不涨就说明点击根本没被系统记录下来，A 阶段测不了，直接 SKIP（不要误报 FAIL）。
[int]$cx = 0; [int]$cy = 0
[void][T5]::Center($bgH, [ref]$cx, [ref]$cy)
$c0 = Get-ClickCounters (Read-Log)
if ($null -eq $c0) {
    # 心跳还没打出来（启动不到 1 秒），再等一轮
    Start-Sleep -Milliseconds 1200
    $c0 = Get-ClickCounters (Read-Log)
}
[T5]::ClickAt($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 1300
$c1 = Get-ClickCounters (Read-Log)
$injectOk = $true
if ($c0 -and $c1) {
    if ($c1.Sample -eq $c0.Sample -and $c1.Hook -eq $c0.Hook) {
        $injectOk = $false
        "注意：注入的点击没有被系统记录（点击采样 $($c0.Sample)→$($c1.Sample)，钩子 $($c0.Hook)→$($c1.Hook)）。"
        "      这台机器上 SendInput 被拦了（WeGame 的 ACE 会拦），A 阶段跳过，只验证 B 阶段。"
    } else {
        "注入探测: 点击采样 $($c0.Sample)→$($c1.Sample)，钩子 $($c0.Hook)→$($c1.Hook)（系统看得见合成点击）"
    }
}
# 探测这一下点的是 BgWin，守卫会记一份"点击证据"；等它老掉（> 350ms）再做 A，
# 否则 A 的判定会直接命中"你点了别的程序"，测不到"证据晚到"这段。
Start-Sleep -Milliseconds 700

if ($injectOk) {
    # 前提：前台必须能被切到 PopTarget（全屏独占游戏/前台锁会拒绝），切不动就 SKIP
    [void][T5]::RaiseFast($ptH)
    Start-Sleep -Milliseconds 400
    if ([T5]::FgProc() -ne 'PopTarget') {
        "跳过：前台切不动（现在是 $([T5]::FgProc())，可能在全屏游戏/独占前台），这一条无法判定。"
        $skipped = $true
    } else {
        # 把前台交给对照组，让 PopTarget 处于"不在前台"的状态
        [void][T5]::RaiseFast($bgH)
        Start-Sleep -Milliseconds 800
        if ([T5]::FgProc() -ne 'BgWin') {
            "跳过：前台切不回对照组（现在是 $([T5]::FgProc())）。"
            $skipped = $true
        }
    }
}

if ($injectOk -and -not $skipped) {
    # 选点击落点
    if ($ClickTarget -eq 'taskbar') {
        $tray = [T5]::FindWindow('Shell_TrayWnd', $null)
        if ($tray -eq [IntPtr]::Zero) { throw '找不到任务栏（Shell_TrayWnd）' }
        $tr = New-Object T5+RECT
        [void][T5]::GetWindowRect($tray, [ref]$tr)
        # 任务栏靠左的空白处（Win11 的图标是居中的；Win10 这里可能是开始按钮）
        $cx = $tr.Left + 20
        $cy = ($tr.Top + $tr.Bottom) / 2
    } else {
        [void][T5]::Center($ptH, [ref]$cx, [ref]$cy)
    }

    $lines = @()
    $before = (Read-Log).Length
    [void][T5]::RaceRaiseAndClick($ptH, $cx, $cy, $ClickDelayMs)
    # 等：P0-B 的 30ms 复查 + 动作线程执行 + 日志异步 flush（200ms）
    Start-Sleep -Milliseconds 1200
    $now = Read-Log
    $partA = $now.Substring([Math]::Min($before, $now.Length))
    $lines = @($partA -split "`r?`n" | Where-Object { $_.Trim() })

    "=== A) 抬前台 + ${ClickDelayMs}ms 后点 $ClickTarget（竞态）之后的日志 ==="
    ($lines | Where-Object { $_ -match '前台变化|放行原因|拦截|不放行|取消拦截' } | Select-Object -Last 8)

    $blocksA = ([regex]::Matches($partA, '拦截 #')).Count
    $lateLog = $partA.Contains('点击证据晚到')
    $clickAllowLog = ($partA.Contains('你点了这个窗口') -or $partA.Contains('你点了任务栏') -or
                      $partA.Contains('你点了开始菜单'))
    $cancelLog = $partA.Contains('取消拦截')
    $fgAfter = [T5]::FgProc()

    "A 断言: 拦截 $blocksA 次，前台现在 = $fgAfter，点击证据晚到留痕 = $lateLog，点击放行留痕 = $clickAllowLog"
    if ($fgAfter -ne 'PopTarget') {
        # 前台根本没切过去：这一轮没有产生"目标窗口抢前台"，断言无意义
        "跳过：抬前台没生效（前台 = $fgAfter），本轮无法判定。"
        $skipped = $true
    } elseif ($blocksA -gt 0) {
        'A 结果: FAIL（证据晚到的那一下被拦了 —— P0-B 没生效，用户点出来又被压回去）'
        $pass = $false
    } elseif ($lateLog -or $clickAllowLog) {
        'A 结果: PASS（0 次拦截，日志里有证据留痕）'
    } elseif ($cancelLog) {
        # 复核取消也算"没压回去"，但要说清是哪条判据取消的（可能前台已经被点击换掉了）
        $why = ($lines | Where-Object { $_ -match '取消拦截' } | Select-Object -Last 1)
        "A 结果: PASS（0 次拦截；执行前复核取消了这次打回：$why）"
    } else {
        'A 结果: FAIL（0 次拦截但日志里既没证据也没取消留痕：无法确认这是"凭证据放行"还是"守卫压根没看见这次切换"）'
        $pass = $false
    }

    # ---------- B) 对照组：只抬前台、不点击 → 必须拦 ----------
    # A 阶段如果走的是"点击放行"，同一个窗口会有 3 秒放行保护（「别把我刚点开的窗口压回去」），
    # 等它过期再测，否则 B 拦不到是符合规则的，不是 bug。
    Start-Sleep -Milliseconds 3600
    $before = (Read-Log).Length
    Set-Content -Path $flag -Value 'cycle'   # PopTarget 先最小化，300ms 后恢复并抢前台（全程无输入）
    Start-Sleep -Seconds 3
    Remove-Item $flag -ErrorAction SilentlyContinue
    $now = Read-Log
    $partB = $now.Substring([Math]::Min($before, $now.Length))
    $linesB = @($partB -split "`r?`n" | Where-Object { $_.Trim() })

    "=== B) 只抬前台、不点击（对照）之后的日志 ==="
    ($linesB | Where-Object { $_ -match '前台变化|放行原因|拦截|不放行|取消拦截' } | Select-Object -Last 8)

    $blocksB = ([regex]::Matches($partB, '拦截 #')).Count
    if ($blocksB -ge 1) { 'B 结果: PASS（无输入的自弹照样被拦，30ms 复查没有把真拦截也一起放掉）' }
    else { 'B 结果: FAIL（没有点击证据的自弹也不拦了 —— 30ms 复查把拦截逻辑废掉了）'; $pass = $false }
}

Cleanup
''
if (-not $pass) { '结果: FAIL'; exit 1 }
elseif ($skipped) { '结果: SKIP（前台切不动 / 注入被拦 / 测试窗口没起来，本轮结论无效）'; exit 0 }
else { '结果: PASS' }
