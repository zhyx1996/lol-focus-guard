# 真机验收：把一段时间的日志读成"结论"，而不是自己去翻日志
#
# 用法（打完一局之后跑）：
#   pwsh ./verify-real.ps1
#   pwsh ./verify-real.ps1 -VerboseLog      # 如果你那局是 --verbose 跑的（判定依据更全）
#
# 它只读日志，不动任何窗口、不影响守卫。检查的是这个项目的两个目标：
#   ① 客户端/WeGame 在后台时自己抢前台 → 有没有被拦住
#   ② 你手动把窗口叫出来（点任务栏/缩略图/托盘/点窗口/Alt+Tab） → 有没有被误拦
#
# 输出分两部分：先给事实（拦了谁、放行了什么原因、有没有放弃拦截/取消拦截），
# 再给判定（✓ / ! / ✗ 和下一步该看什么）。
param(
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'),
    [switch]$VerboseLog
)
$ErrorActionPreference = 'Stop'

function Read-Log([string]$path) {
    if (-not (Test-Path $path)) { return @() }
    try { return @(Get-Content $path -Encoding UTF8) } catch { return @() }
}

$lines = Read-Log $LogPath
$rotated = Read-Log ($LogPath + '.1')
$sources = @()
if ($rotated.Count -gt 0) { $sources += "guard.log.1（上一份，$($rotated.Count) 行）" }
if ($lines.Count -gt 0) { $sources += "guard.log（$($lines.Count) 行）" }

if ($lines.Count -eq 0 -and $rotated.Count -eq 0) {
    "找不到日志：$LogPath"
    '先运行 LolFocusGuard.exe（托盘图标在就说明在跑），打一局再回来跑这个脚本。'
    exit 1
}

# 一份日志里"上一份"在前，合并进来保证跨轮转的连续判断
$all = @()
$all += $rotated
$all += $lines

$firstTime = $null; $lastTime = $null
$blockByWindow = @{}
$allowReasons = @{}
$giveUp = 0
$cancel = 0
$cancelReasons = @{}
$blockFail = 0
$clientStarts = 0
$clientStops = 0
$sessionBlocks = @()
$heartbeats = 0
$unlockAllows = 0
$wegameLoginAllows = 0
$switchEvents = 0

foreach ($raw in $all) {
    $line = [string]$raw
    if ($line.Length -lt 26) { continue }
    if ($line[0] -eq '[') {
        $t = $line.Substring(1, 23)
        if ($null -eq $firstTime) { $firstTime = $t }
        $lastTime = $t
    }

    if ($line -match '拦截 #\d+：([^\s(]+)') {
        $w = $Matches[1]
        if ($blockByWindow.ContainsKey($w)) { $blockByWindow[$w]++ } else { $blockByWindow[$w] = 1 }
        continue
    }
    if ($line -match '放行原因: ([^（]+)') {
        $r = $Matches[1].Trim()
        if ($allowReasons.ContainsKey($r)) { $allowReasons[$r]++ } else { $allowReasons[$r] = 1 }
        continue
    }
    if ($line -match '放弃拦截 15 秒') { $giveUp++; continue }
    if ($line -match '取消拦截（请求 #\d+）：(.+)$') {
        $cancel++
        $why = $Matches[1]
        # 归一化：把毫秒数和指针落点挖掉，只留原因大类
        $why = $why -replace '\d+(\.\d+)?ms前', 'Nms前' -replace '（[^）]*@[^）]*）', '（落点）'
        if ($why.IndexOf(' (pid=') -gt 0) { $why = $why.Substring(0, $why.IndexOf(' (pid=')) }
        if ($cancelReasons.ContainsKey($why)) { $cancelReasons[$why]++ } else { $cancelReasons[$why] = 1 }
        continue
    }
    if ($line -match '打回失败|ShowWindow 被挡|压不下去') { $blockFail++; continue }
    if ($line -match '监测到客户端/WeGame 已启动') { $clientStarts++; continue }
    if ($line -match '客户端/WeGame 已全部退出：本轮拦截 (\d+) 次') { $clientStops++; $sessionBlocks += [int]$Matches[1]; continue }
    if ($line -match '心跳 ') {
        $heartbeats++
        if ($line -match '切换器事件=(\d+)次') { $switchEvents = [int]$Matches[1] }
        continue
    }
    if ($line -match '放行（不是拦截）：刚解锁') { $unlockAllows++; continue }
    if ($line -match '放行 WeGame 窗口（登录阶段') { $wegameLoginAllows++; continue }
}

$totalBlocks = 0
foreach ($k in $blockByWindow.Keys) { $totalBlocks += $blockByWindow[$k] }
$totalAllows = 0
foreach ($k in $allowReasons.Keys) { $totalAllows += $allowReasons[$k] }

"=== LolFocusGuard 真机验收 ==="
"日志: $($sources -join ' + ')"
"时间范围: $firstTime → $lastTime"
''
"拦截: $totalBlocks 次"
foreach ($k in ($blockByWindow.GetEnumerator() | Sort-Object -Property Value -Descending)) {
    "  $($k.Key) ×$($k.Value)"
}
if ($totalBlocks -eq 0) { '  （这次一段都没拦 —— 见下面判定）' }
''
"放行（有原因记录的）: $totalAllows 次"
foreach ($k in ($allowReasons.GetEnumerator() | Sort-Object -Property Value -Descending)) {
    "  $($k.Key) ×$($k.Value)"
}
if ($totalAllows -eq 0) { '  （普通日志只记部分放行原因，想看全给 --verbose 跑一局）' }
''
"客户端启动/退出: $clientStarts / $clientStops 段" + $(if ($sessionBlocks.Count -gt 0) { "，各段拦截分别: $($sessionBlocks -join ', ') 次" } else { '' })
if ($wegameLoginAllows -gt 0) { "WeGame 登录阶段放行: $wegameLoginAllows 次" }
if ($unlockAllows -gt 0) { "解锁/恢复会话放行: $unlockAllows 次" }
"放弃拦截（10 秒内 5 次压不动 → 停手 15 秒）: $giveUp 次"
"取消拦截（排队后才发现你已经接管）: $cancel 次"
if ($cancel -gt 0) {
    foreach ($k in ($cancelReasons.GetEnumerator() | Sort-Object -Property Value -Descending)) { "  $($k.Key) ×$($k.Value)" }
}
"打回失败/被挡: $blockFail 次"
if ($heartbeats -gt 0) { "心跳行: $heartbeats 行（说明这局是 --verbose 跑的；切换器事件累计=$switchEvents）" }
''

# ---------------- 判定 ----------------
$clientRunningBlocks = 0
foreach ($k in $blockByWindow.Keys) {
    if ($k -match 'League|Riot|wegame|WeGame|browser|pallas') { $clientRunningBlocks += $blockByWindow[$k] }
}
$clickAllows = 0
foreach ($k in $allowReasons.Keys) {
    if ($k -match '你点了|你按了 Win|切换器|Alt') { $clickAllows += $allowReasons[$k] }
}

"=== 判定 ==="
if ($totalBlocks -gt 0 -or $clientStarts -gt 0 -or $heartbeats -gt 0) {
    '✓ 守卫这段是真在工作的（有拦截记录/客户端起退记录/心跳）'
} else {
    '✗ 这段日志里既没有拦截、也没有客户端起退记录、也没有心跳 —— 守卫可能根本没在跑，或这期间客户端没起来'
}

if ($clientRunningBlocks -gt 0) {
    "✓ 客户端/WeGame 在后台自己抢前台时被拦了 $clientRunningBlocks 次（① 目标达成方向）"
} elseif ($clientStarts -gt 0) {
    '! 客户端起来过，但一次都没拦 —— 如果这局它确实弹过（比如"匹配成功"抢了前台），把这次日志发我'
} else {
    '· 这段没有客户端运行记录，① 这次没法验证'
}

if ($clickAllows -gt 0) {
    "✓ 手动切窗口被识别成你的操作（$clickAllows 次）→ 没有把你自己的点击当自弹（② 目标达成方向）"
} elseif ($totalBlocks -gt 0) {
    '! 有拦截但没记录到"你点了…"的放行：如果你这局手动切过窗口却没被记，说明点击没被识别，要查（普通日志只记部分原因）'
} else {
    '· 这段没有手动切换记录，② 这次没法验证'
}

if ($giveUp -gt 0) {
    "! 有 $giveUp 次「放弃拦截」：说明连续 5 次没能把前台拿回来，那 15 秒里它是被放行的。把这份日志发我，我按具体窗口查为什么压不动"
}
if ($cancel -gt 0) {
    "! 有 $cancel 次「取消拦截」：如果这些不是你按 Win/点任务栏造成的，就是偶发漏拦，把带原因的那段发我"
}
if ($blockFail -gt 0) {
    "! 有 $blockFail 次「打回失败」：窗口没被最小化掉（可能被系统策略挡住），发我这段日志"
}
if ($giveUp -eq 0 -and $cancel -eq 0 -and $blockFail -eq 0 -and $totalBlocks -gt 0) {
    '✓ 没有放弃拦截、没有取消拦截、没有打回失败 —— 拦得干净'
}
if (-not $VerboseLog -and $heartbeats -eq 0) {
    ''
    '提示：这次是普通日志，能看"拦了谁/放行了什么"，但看不到每次判定的完整依据。'
    '     想深挖某一局，用 --verbose 启动守卫再打一局，然后跑 pwsh ./verify-real.ps1 -VerboseLog'
}
