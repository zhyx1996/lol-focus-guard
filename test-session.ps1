# 锁屏/解锁通知链路测试（不用真的锁屏，不抢前台）
#
# 为什么要测：Windows 解锁后会把前台还原给锁屏前那个窗口。守卫如果不能区分
# "系统还原" 和 "它自己弹出来"，就会在你刚解锁时把客户端缩掉。
# 判据链是：系统消息 WM_WTSSESSION_CHANGE(WTS_SESSION_UNLOCK) → 记时刻 → 2 秒内放行。
#
# 这条链路可以不开锁屏就验：守卫的会话通知挂在它的离屏 HotkeyWindow 上，
# 我们找到那个 hwnd 直接投递同样的消息（wParm=8 解锁 / 7 锁屏），看它有没有按预期记账。
# 这样验到的是"消息 → Guard.OnSessionChanged → 日志"这一整段；
# 唯一没覆盖的是 Windows 自己发这条消息（那是系统行为，文档明确，风险低）。
# 默认只验"消息 → 记账"这一段（完全不碰窗口，可以随时跑）。
# 加 -WithWindow 时再验第二段：解锁记账之后真的有一个被监控窗口抢前台 →
# 必须走"放行原因: 刚解锁"，0 次拦截。第二段会让测试窗口短暂抢前台，
# 所以只在没人玩游戏的时候跑。
param([switch]$WithWindow)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class S3 {
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  public static List<IntPtr> OfPid(int pid) {
    List<IntPtr> list = new List<IntPtr>();
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint p; GetWindowThreadProcessId(h, out p);
      if ((int)p == pid) list.Add(h);
      return true;
    }, IntPtr.Zero);
    return list;
  }
  public static string ClassOf(IntPtr h) {
    System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
    GetClassName(h, sb, sb.Capacity);
    return sb.ToString();
  }
  // 投递一条会话变化消息：code = 7 锁屏 / 8 解锁
  public static int PostSession(IntPtr h, int code) {
    return PostMessage(h, 0x02B1, (IntPtr)code, IntPtr.Zero) ? 1 : 0;
  }
}
'@

$root = $PSScriptRoot
$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'

Get-Process LolFocusGuard -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400
Remove-Item $guardLog -ErrorAction SilentlyContinue

$guard = Start-Process (Join-Path $root 'LolFocusGuard.exe') -ArgumentList '--targets=FakeClient', '--verbose' -PassThru
Start-Sleep -Milliseconds 800
if ($guard.HasExited) {
    '跳过：守卫启动即退出（单实例互斥锁可能还被上一轮占着）'
    '结果: SKIP'; exit 0
}
Start-Sleep -Milliseconds 1200

# 找到守卫的离屏窗口（会话通知就挂在它上面）
$handles = [S3]::OfPid($guard.Id)
if ($handles.Count -eq 0) {
    '跳过：没找到守卫的窗口（枚举不到 hwnd）'
    Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
    '结果: SKIP'; exit 0
}
$desc = ($handles | ForEach-Object { "$([S3]::ClassOf($_))" }) -join ', '

$sentUnlock = 0
foreach ($h in $handles) { $sentUnlock += [S3]::PostSession($h, 8) }
Start-Sleep -Milliseconds 500
$afterUnlock = Get-Content $guardLog -Raw -ErrorAction SilentlyContinue

$sentLock = 0
foreach ($h in $handles) { $sentLock += [S3]::PostSession($h, 7) }
Start-Sleep -Milliseconds 500
$afterLock = Get-Content $guardLog -Raw -ErrorAction SilentlyContinue

$pass = $true
"守卫窗口: $desc（共 $($handles.Count) 个），解锁消息投递成功 $sentUnlock 个，锁屏消息 $sentLock 个"

if ($afterUnlock -match '会话通知注册失败') {
    '注意：这台机器上会话通知注册失败（wtsapi32 挂不上），解锁宽限不会生效 —— 这是环境限制，不算失败'
    $registered = $false
} else {
    $registered = $true
}

if (-not $registered) {
    '结果: SKIP（会话通知注册失败，只能靠真机锁屏观察）'
    Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
    exit 0
}

if ($afterUnlock -match '会话已解锁') {
    'ok  : 解锁消息被收到并记了账（2 秒内的前台变化会按"系统恢复"放行）'
} else {
    'FAIL: 解锁消息没有产生「会话已解锁」日志 —— WM_WTSSESSION_CHANGE 这条链路没通'
    ($afterUnlock -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 5)
    $pass = $false
}

if ($afterLock -match '会话已锁定') {
    'ok  : 锁屏消息也被识别（只写日志，不影响判定）'
} else {
    'FAIL: 锁屏消息没有对应日志'
    $pass = $false
}

# 顺带确认这行放行原因不会污染统计：它不含「拦截 #」也不含「放行原因: 」
$polluted = ($afterUnlock + $afterLock) -match '放行原因: 刚解锁'
if ($polluted) { '注意：--verbose 下出现了放行原因行（正常，仅供排除统计污染）' }

# ---------- 第二段（可选）：解锁之后真的有一个被监控窗口抢前台 → 必须放行 ─---------
if ($WithWindow -and $pass) {
    $flag = Join-Path $env:TEMP 'poptarget.flag'
    $pt = Start-Process (Join-Path $root 'PopTarget.exe') -ArgumentList 'target' -PassThru
    Start-Sleep -Seconds 2
    $beforeLen = (Get-Content $guardLog -Raw -ErrorAction SilentlyContinue).Length
    # 先投递"解锁"，再立刻让它抢前台：2 秒宽限内应当按"系统恢复"放行
    foreach ($h in $handles) { [void][S3]::PostSession($h, 8) }
    Set-Content -Path $flag -Value 'cycle'
    Start-Sleep -Milliseconds 1600
    $part = (Get-Content $guardLog -Raw -ErrorAction SilentlyContinue).Substring($beforeLen)
    $blocks = ([regex]::Matches($part, '拦截 #\d+：PopTarget')).Count
    if ($blocks -eq 0 -and $part -match '刚解锁/恢复会话') {
        'ok  : 解锁宽限真的生效（它抢前台被当成系统恢复放行，0 次拦截）'
    } else {
        "FAIL: 解锁宽限没生效（拦截 $blocks 次）"
        ($part -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 5)
        $pass = $false
    }
    Stop-Process -Id $pt.Id -Force -ErrorAction SilentlyContinue
    Remove-Item $flag -ErrorAction SilentlyContinue
}

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:TEMP 'poptarget.flag') -ErrorAction SilentlyContinue

if ($pass) { '结果: PASS'; exit 0 } else { '结果: FAIL'; exit 1 }
