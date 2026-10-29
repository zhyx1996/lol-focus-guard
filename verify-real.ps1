# 真机验收：把一段时间的日志读成"结论"，而不是自己去翻日志
#
# 用法（打完一局之后跑）：
#   pwsh ./verify-real.ps1
#   pwsh ./verify-real.ps1 -VerboseLog      # 如果你那局是 --verbose 跑的（判定依据更全）
#   pwsh ./verify-real.ps1 -LastMinutes 40  # 只看最近 40 分钟（一局大概这么久；避免把上一局也算进来）
#   pwsh ./verify-real.ps1 -LogPath "$env:LOCALAPPDATA\LolFocusGuard\archive-20261029-2334.log"  # 看归档日志
#
# 它只读日志，不动任何窗口、不影响守卫。检查的是这个项目的两个目标：
#   ① 客户端/WeGame 在后台时自己抢前台 → 有没有被拦住
#   ② 你手动把窗口叫出来（点任务栏/缩略图/托盘/点窗口/Alt+Tab） → 有没有被误拦
#
# 输出分两部分：先给事实（拦了谁、放行了什么原因、有没有放弃拦截/取消拦截），
# 再给判定（✓ / ! / ✗ 和下一步该看什么）。
param(
    [string]$LogPath = (Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'),
    [switch]$VerboseLog,
    # 只看最近这么多分钟的行（0 = 整份日志）。
    # 打完一局来验收时用它：否则同一份日志里的上一局、甚至今天调 bug 的几百行都会一起被统计，
    # 明明这一局干净，报告里却冒出"上一次的取消拦截/打回失败"。
    [int]$LastMinutes = 0
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

# -LastMinutes：只留时间戳落在窗口内的行（认不出时间戳的行保留，宁可多算也不静默丢）
$filteredOut = 0
if ($LastMinutes -gt 0) {
    $cutoff = (Get-Date).AddMinutes(-$LastMinutes)
    $kept = New-Object System.Collections.ArrayList
    foreach ($ln in $all) {
        $t = $null
        if ($ln.Length -ge 25 -and $ln[0] -eq '[') {
            try { $t = [datetime]::ParseExact($ln.Substring(1, 23), 'yyyy-MM-dd HH:mm:ss.fff', $null) } catch { $t = $null }
        }
        if ($null -eq $t -or $t -ge $cutoff) { [void]$kept.Add($ln) } else { $filteredOut++ }
    }
    $all = @($kept)
    "只统计最近 $LastMinutes 分钟（{0:HH:mm:ss} 之后）：留下 {1} 行，滤掉 {2} 行更早的" -f $cutoff, $all.Count, $filteredOut
    ''
}

$firstTime = $null; $lastTime = $null
$blockByWindow = @{}
$allowReasons = @{}
$giveUp = 0
$cancel = 0
$cancelReasons = @{}
# 打回动作的三种结果（措辞以 LolFocusGuard.cs 为准；匹配只认关键词，容忍后半句改动）：
#   「最小化失败（ShowWindow 和 WM_SYSCOMMAND 都没生效）」→ 真没压下去
#   「ShowWindow 被挡，改用 WM_SYSCOMMAND/SC_MINIMIZE 最小化成功」→ 兜底生效，是**成功**
#   「打回失败: 前台没能交还」→ 最小化了，但焦点没还给你（全屏独占挡 SetForegroundWindow）
$minFail = 0
$fallbackMin = 0
$handoffFail = 0
# 两条动作副作用诊断（守卫里只在 --verbose 下写）
$diagModal = 0; $diagModalLines = @()      # 打回时窗口正挂着模态弹窗（IsWindowEnabled=False）
$diagFgNotRestored = 0; $diagFgLines = @() # 打回后前台没回到预期窗口
$diagOther = 0; $diagOtherLines = @()      # 认不出来的诊断行（守卫改措辞后就落这里，不静默丢）
$autoOther = 0; $autoOtherLines = @()      # 含「打回」但三档都没认出来的行
$clientStarts = 0
$clientStops = 0
$sessionBlocks = @()
$heartbeats = 0
$unlockAllows = 0
$wegameLoginAllows = 0
$switchEvents = 0
# 任务切换器（Alt+Tab 浮层 / 任务视图）认出来的次数，以及靠"一次性切换凭据"放行的次数。
# 这台 Win11 上 EVENT_SYSTEM_SWITCH* 一次都不发（心跳里 切换器事件=0），所以切换动作
# 只能按窗口身份认 —— 这两个数就是那条通道到底有没有在用的证据。
$switcherSeen = 0
$credAllow = 0
# "按住"（打回后 300ms 内反复压）：命中次数 + 累计压回去的次数。
# 这是"对局就绪"那种被打回后几十毫秒又顶回来的场景到底有没有被压住的直接证据。
$holdRounds = 0
$holdPushes = 0
# 打回时窗口带着模态弹窗（IsWindowEnabled=False）的次数 —— 这类现在只抢前台、不最小化，
# 出现这个数说明你打游戏的时候客户端确实弹过"接受对局"之类的东西。
$freeModal = 0
# WeGame 启动流程宽限（客户端刚起来 20 秒内放行 WeGame 自己的窗口）
$launchGrace = 0
# "放弃拦截期"（10 秒内连续 5 次压不下去 → 15 秒不设防）：唯一会主动不设防的情况
$suppressEnters = 0
# 只有"你在操作"的放行才参与复发配对。抑制期（正在放弃拦截期）、刚放行过这个窗口、刚解锁
# 这些不是用户意图：拿它们配对必然误报 —— 抑制期一结束它再弹就被拦，间隔往往 <1.2 秒，
# 结果被算成"你刚把它叫出来又被压回去"，而用户根本没叫过它。
$userIntentPattern = '你点了|你按了 Win|切换器|Alt|客户端窗口第一次出现|WeGame 登录阶段'
# 「用户操作后又被压回去」的复发：用户意图放行之后 1.2 秒内，同一进程的窗口又被拦截。
# 注意：同一 hwnd 在用户放行后 3 秒内不可能被再拦（守卫有 SameWindowJustAllowed 保护），
# 所以这条命中只可能是"同进程的另一个窗口"或"抑制期尾巴"，只作提示（!），不当必失败。
$relapse = 0
$relapseSamples = @()
# 同一个窗口 10 秒内被拦 >= 5 次（拉锯）
$burstWindows = @{}
$lastAllowAt = $null; $lastAllowProc = ''
$lastFgProc = ''
$fgChangeLines = 0
$blockTimes = @{}

foreach ($raw in $all) {
    $line = [string]$raw
    if ($line.Length -lt 26) { continue }
    $tsNow = $null
    if ($line[0] -eq '[') {
        $t = $line.Substring(1, 23)
        if ($null -eq $firstTime) { $firstTime = $t }
        $lastTime = $t
        try { $tsNow = [datetime]::ParseExact($t, 'yyyy-MM-dd HH:mm:ss.fff', $null) } catch { $tsNow = $null }
    }

    # 进程名可能带空格/中文：截断点放在 (pid= 上，不能按空白切（否则带空格的名字会被截成第一个词，
    # 第二遍「漏网目标」的正则会整行不匹配、连 $prevProc 都不更新）
    if ($line -match '前台变化 → (.+?) \(pid=') { $lastFgProc = $Matches[1]; $fgChangeLines++; continue }
    if ($line -match '拦截 #\d+：(.+?) \(pid=') {
        $w = $Matches[1]
        if ($blockByWindow.ContainsKey($w)) { $blockByWindow[$w]++ } else { $blockByWindow[$w] = 1 }
        # 复发检测：刚放行过这个窗口，1.2 秒内又被压回去 = 用户最痛的那个症状
        if ($null -ne $tsNow -and $null -ne $lastAllowAt -and $w -eq $lastAllowProc) {
            $delta = ($tsNow - $lastAllowAt).TotalMilliseconds
            if ($delta -ge 0 -and $delta -le 1200) {
                $relapse++
                if ($relapseSamples.Count -lt 3) {
                    $relapseSamples += ("{0} → {1}，间隔 {2}ms" -f $lastAllowAt.ToString('HH:mm:ss.fff'), $tsNow.ToString('HH:mm:ss.fff'), [int]$delta)
                }
            }
        }
        # 拉锯检测：同一个窗口 10 秒内被拦 >= 5 次，说明压不住、一直在打
        if ($null -ne $tsNow) {
            if (-not $blockTimes.ContainsKey($w)) { $blockTimes[$w] = @() }
            $recent = @($blockTimes[$w] | Where-Object { ($tsNow - $_).TotalMilliseconds -le 10000 })
            $recent += $tsNow
            $blockTimes[$w] = $recent
            if ($recent.Count -ge 5) { $burstWindows[$w] = $true }
        }
        continue
    }
    if ($line -match '放行原因: ([^（]+)') {
        $r = $Matches[1].Trim()
        if ($allowReasons.ContainsKey($r)) { $allowReasons[$r]++ } else { $allowReasons[$r] = 1 }
        if ($r -match 'Alt\+Tab|一次性凭据|任务视图') { $credAllow++ }
        # 记下"这次放的是谁"：放行行本身不带进程名，靠上一行「前台变化 → <proc>」配对
        # （普通日志没有「前台变化」行，所以复发检测只在 --verbose 日志上有效）
        if ($null -ne $tsNow -and $lastFgProc -ne '') {
            # 只有用户意图的放行才记账；抑制期/刚放行过/刚解锁这类不参与（否则必然误报）
            if ($r -match $userIntentPattern) {
                $lastAllowAt = $tsNow
                $lastAllowProc = $lastFgProc
            }
            # 配对用掉就清掉：一行放行只能对上它前面那一次前台变化，
            # 否则后面另一个窗口的放行会错误地沿用旧进程名，把"复发"算错。
            $lastFgProc = ''
        }
        continue
    }
    if ($line -match '放弃拦截 15 秒') { $giveUp++; continue }
    # 新增机制的证据行（守卫 b9db24c / e132534 / 0c50c26）
    if ($line -match '按住：它又抢了 (\d+) 次前台') {
        $holdRounds++
        $holdPushes += [int]$Matches[1]
        continue
    }
    if ($line -match '带着模态弹窗|模态窗口只抢前台不最小化') { $freeModal++; continue }
    if ($line -match 'WeGame 启动流程') { $launchGrace++; continue }
    if ($line -match '正在放弃拦截期') { $suppressEnters++; continue }
    if ($line -match '取消拦截（请求 #\d+）：(.+)$') {
        $cancel++
        $why = $Matches[1]
        # 归一化：把毫秒数和指针落点挖掉，只留原因大类
        $why = $why -replace '\d+(\.\d+)?ms前', 'Nms前' -replace '（[^）]*@[^）]*）', '（落点）'
        if ($why.IndexOf(' (pid=') -gt 0) { $why = $why.Substring(0, $why.IndexOf(' (pid=')) }
        if ($cancelReasons.ContainsKey($why)) { $cancelReasons[$why]++ } else { $cancelReasons[$why] = 1 }
        continue
    }
    # 两条"动作副作用"诊断（守卫 8577447 / 7db4966，只 --verbose 写）。
    # 放在「打回」系列判断之前：这两行的正文里含「打回」字样，先吃掉免得被归错档。
    # 只认 "[时间戳] 诊断：" 这个前缀 + 关键字，认不出来的进"其他诊断"档 —— 守卫以后改措辞也不会
    # 让诊断行悄悄消失（宁可多一栏，不能一条都不显示）。
    if ($line -match '\] 诊断：') {
        if ($line -match '不可交互|模态') {
            $diagModal++
            if ($diagModalLines.Count -lt 3) { $diagModalLines += ($line -replace '^\[(\d{4}-\d{2}-\d{2}) ', '[') }
        } elseif ($line -match '预期窗口|预期 ') {
            $diagFgNotRestored++
            if ($diagFgLines.Count -lt 3) { $diagFgLines += ($line -replace '^\[(\d{4}-\d{2}-\d{2}) ', '[') }
        } else {
            $diagOther++
            if ($diagOtherLines.Count -lt 3) { $diagOtherLines += ($line -replace '^\[(\d{4}-\d{2}-\d{2}) ', '[') }
        }
        continue
    }
    # 打回动作的三种结果，分开计（顺序不能换：「最小化失败」那行也含 ShowWindow 字样）
    if ($line -match '最小化失败') { $minFail++; continue }
    if ($line -match 'ShowWindow 被挡' -or ($line -match 'SC_MINIMIZE' -and $line -match '最小化成功')) { $fallbackMin++; continue }
    if ($line -match '打回失败') { $handoffFail++; continue }
    if ($line -match '打回细节') { continue }   # 已知的可选细节行，不属于任何一档结果
    if ($line -match '打回') {                  # 措辞变了、上面三档都没认出来 → 单独露面，不要静默丢掉
        $autoOther++
        if ($autoOtherLines.Count -lt 3) { $autoOtherLines += ($line -replace '^\[(\d{4}-\d{2}-\d{2}) ', '[') }
        continue
    }
    if ($line -match '监测到客户端/WeGame 已启动') { $clientStarts++; continue }
    if ($line -match '客户端/WeGame 已全部退出：本轮拦截 (\d+) 次') { $clientStops++; $sessionBlocks += [int]$Matches[1]; continue }
    if ($line -match '识别到任务切换器') { $switcherSeen++; continue }
    if ($line -match '心跳 ') {
        $heartbeats++
        if ($line -match '切换器事件=(\d+)次') { $switchEvents = [int]$Matches[1] }
        continue
    }
    if ($line -match '放行（不是拦截）：刚解锁') { $unlockAllows++; continue }
    if ($line -match '放行 WeGame 窗口（登录阶段') { $wegameLoginAllows++; continue }
}

# ---------- 第二遍：你没有任何输入时，谁把前台抢走了（只有 --verbose 日志有「前台变化」行） ----------
# 这是"漏网目标"的发现手段：如果某个窗口在你没输入时抢了前台、而它又没被拦，
# 说明它不在目标名单里 —— 名字就在这一行的 proc/class/标题里。
$stealers = [ordered]@{}
$shellProcs = @('explorer', 'StartMenuExperienceHost', 'ShellExperienceHost', 'SearchHost', 'LolFocusGuard', '')
$prevProc = ''; $prevTitle = ''
foreach ($raw in $all) {
    $line = [string]$raw
    if ($line -notmatch '前台变化 → (.+?) \(pid=\d+\) class=(\S*) 标题="([^"]*)" 尺寸=(\S+) 最小化=(\w+) 按键=(.+)$') { continue }
    $proc = $Matches[1]; $cls = $Matches[2]; $title = $Matches[3]; $size = $Matches[4]; $minimized = $Matches[5]; $keyInfo = $Matches[6]
    $idle = ($keyInfo -match '没按过或已用掉')
    $fromReal = ($prevProc -ne '' -and ($shellProcs -notcontains $prevProc))
    $isMinimizeArtifact = ($size -like '-32000*' -or $minimized -eq 'True' -or $size -like '0x0*')
    if ($idle -and $fromReal -and $proc -ne $prevProc -and ($shellProcs -notcontains $proc) -and -not $isMinimizeArtifact) {
        $k = "$proc  class=$cls  标题=""$title"""
        if ($stealers.Contains($k)) { $stealers[$k]++ } else { $stealers[$k] = 1 }
    }
    $prevProc = $proc; $prevTitle = $title
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
if ($totalAllows -eq 0) {
    if ($fgChangeLines -gt 0 -or $heartbeats -gt 0) { '  （这局一次放行都没记录到 —— 如果它确实弹过、你也手动切过，那不正常，发我日志）' }
    else { '  （普通日志只记部分放行原因，想看全给 --verbose 跑一局）' }
}
''
"客户端启动/退出: $clientStarts / $clientStops 段" + $(if ($sessionBlocks.Count -gt 0) { "，各段拦截分别: $($sessionBlocks -join ', ') 次" } else { '' })
if ($wegameLoginAllows -gt 0) { "WeGame 登录阶段放行: $wegameLoginAllows 次" }
if ($unlockAllows -gt 0) { "解锁/恢复会话放行: $unlockAllows 次" }
if ($stealers.Count -gt 0) {
    '疑似"没被拦住"的抢前台（你没有任何输入时把前台抢走的窗口）:'
    foreach ($k in ($stealers.GetEnumerator() | Sort-Object -Property Value -Descending)) {
        # 这一段该进程被拦过几次（按进程名粗配）
        $pname = ($k.Key -split '  ')[0]
        $b = 0; if ($blockByWindow.ContainsKey($pname)) { $b = $blockByWindow[$pname] }
        "  $($k.Key) ×$($k.Value)（这段被拦 $b 次）"
    }
}
if ($relapse -gt 0) {
    "用户操作后 1.2 秒内同进程的窗口又被拦: $relapse 次（同一窗口 3 秒内不可能被再拦，只可能是同进程另一个窗口或抑制期尾巴）"
    foreach ($rs in $relapseSamples) { "  $rs" }
}
if ($burstWindows.Count -gt 0) {
    "10 秒内同一窗口被拦 ≥5 次（压不住、在拉锯）: " + (($burstWindows.Keys) -join ', ')
}
"放弃拦截（10 秒内 5 次压不动 → 停手 15 秒）: $giveUp 次"
"取消拦截（排队后才发现你已经接管）: $cancel 次"
if ($cancel -gt 0) {
    foreach ($k in ($cancelReasons.GetEnumerator() | Sort-Object -Property Value -Descending)) { "  $($k.Key) ×$($k.Value)" }
}
"最小化失败（ShowWindow 和 SC_MINIMIZE 都没生效，真没压下去）: $minFail 次"
"ShowWindow 被挡 → 改用 SC_MINIMIZE 成功（兜底生效，不算失败）: $fallbackMin 次"
"前台没能交还（已最小化，但焦点没回到你原来的窗口）: $handoffFail 次"
if ($autoOther -gt 0) {
    "打回相关但认不出类型的行（守卫可能改了措辞，点这条日志）: $autoOther 行"
    foreach ($l in $autoOtherLines) { "  $l" }
}
if ($heartbeats -gt 0) { "心跳行: $heartbeats 行（说明这局是 --verbose 跑的；切换器事件累计=$switchEvents）" }
if ($switcherSeen -gt 0 -or $credAllow -gt 0) {
    "任务切换器认出 $switcherSeen 次；靠一次性切换凭据放行 $credAllow 次（Alt+Tab/任务视图切到客户端、它慢半拍才画出来时，走的就是这条）"
}
if ($holdRounds -gt 0) {
    "按住生效 $holdRounds 次，累计把「又顶回前台」压回去 $holdPushes 次（$([math]::Round($holdPushes / $holdRounds, 1)) 次/轮）——它就是被压住的，不是被放过的"
}
if ($freeModal -gt 0) {
    "打回时窗口带着模态弹窗 $freeModal 次 —— 这些按新规则只抢前台、不最小化，不会藏进任务栏（你要接受随时点任务栏/Alt+Tab）"
}
if ($launchGrace -gt 0) {
    "WeGame 启动流程宽限放行 $launchGrace 次（客户端刚起来 20 秒内，WeGame 自己的窗口不算自弹）"
}
if ($suppressEnters -gt 0) {
    "! 进入「放弃拦截期」$suppressEnters 次（10 秒内连续 5 次压不下去 → 15 秒不设防）：这是唯一会主动不设防的情况，看你当时在做什么"
}
if ($diagModal -gt 0 -or $diagFgNotRestored -gt 0 -or $diagOther -gt 0) {
    ''
    '--- 动作副作用诊断（--verbose 才记；真机上出现了要当回事）---'
    if ($diagModal -gt 0) {
        "打回时窗口正挂着模态弹窗（IsWindowEnabled=False）: $diagModal 次 —— 这种弹窗可能是「接受对局」之类必须点的东西"
        foreach ($l in $diagModalLines) { "  $l" }
    }
    if ($diagFgNotRestored -gt 0) {
        "打回后前台没回到预期窗口: $diagFgNotRestored 次 —— 若当时是全屏独占游戏，表现为「游戏突然不动，得点一下鼠标」"
        foreach ($l in $diagFgLines) { "  $l" }
    }
    if ($diagOther -gt 0) {
        "认不出类型的诊断行: $diagOther 行 —— 守卫可能加了新诊断，点这条日志"
        foreach ($l in $diagOtherLines) { "  $l" }
    }
}
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

if ($switcherSeen -gt 0 -and $credAllow -eq 0) {
    "· 认出了 $switcherSeen 次任务切换器但没用上凭据：多半是你 Alt+Tab 后目标 1.5 秒内就上来了（被「Alt/Win 切换」那条先放行，属于正常）"
}
if ($clickAllows -gt 0) {
    "✓ 手动切窗口被识别成你的操作（$clickAllows 次）→ 没有把你自己的点击当自弹（② 目标达成方向）"
} elseif ($totalBlocks -gt 0) {
    '! 有拦截但没记录到"你点了…"的放行：如果你这局手动切过窗口却没被记，说明点击没被识别，要查（普通日志只记部分原因）'
} else {
    '· 这段没有手动切换记录，② 这次没法验证'
}

# 有"没输入却抢到前台、且这段一次没被拦"的窗口 → 可能漏网目标
# 只看"长得像 LoL/WeGame 那一族"的：游戏自己回前台（比如你在玩的另一个游戏）不算漏网。
$unblockedStealers = @()
foreach ($k in $stealers.Keys) {
    $pname = ($k -split '  ')[0]
    $looksLikeTarget = ($pname -match '^(LeagueClientUx|LeagueClient|RiotClientServices|wegame|wegame_env|pallas|Client|Tencent|tgp)$') -or
                       ($k -match 'WeGame') -or ($k -match 'League')
    if (-not $looksLikeTarget) { continue }
    $b = 0; if ($blockByWindow.ContainsKey($pname)) { $b = $blockByWindow[$pname] }
    if ($b -eq 0 -and $stealers[$k] -ge 2) { $unblockedStealers += $k }
}
if ($unblockedStealers.Count -gt 0) {
    "! 有像客户端/WeGame 的窗口在你没输入时抢了前台、这段一次都没被拦：" + ($unblockedStealers -join '；')
    '  如果客户端当时在跑，这就是漏网的目标（名字就在上面那行里）→ 把这段日志发我，我加进识别；'
    '  如果客户端还没起来（WeGame 登录阶段），按设计就是放行。'
}
if ($relapse -gt 0) {
    "! 有 $relapse 次『你操作完 1.2 秒内同进程的窗口又被拦』：**不是必失败** —— 同一窗口在用户放行后 3 秒内不可能被再拦（SameWindowJustAllowed 保护），所以这里只可能是①同进程的另一个窗口自己弹出来（拦对了）或②放弃拦截期刚结束的尾巴。把这几段时间点发我，我对着日志确认"
}
if ($burstWindows.Count -gt 0) {
    "! 有窗口 10 秒内被拦 ≥5 次：它在反复抢、我们反复压（拉锯）。把这段日志发我，看是不是该放弃得更早/换更硬的手段"
}
if ($giveUp -gt 0) {
    "! 有 $giveUp 次「放弃拦截」：说明连续 5 次没能把前台拿回来，那 15 秒里它是被放行的。把这份日志发我，我按具体窗口查为什么压不动"
}
if ($cancel -gt 0) {
    "! 有 $cancel 次「取消拦截」：如果这些不是你按 Win/点任务栏造成的，就是偶发漏拦，把带原因的那段发我"
}
if ($minFail -gt 0) {
    "! 有 $minFail 次「最小化失败」：ShowWindow 和 SC_MINIMIZE 都没生效，窗口没被压下去，发我这段日志"
}
if ($handoffFail -gt 0) {
    "! 有 $handoffFail 次「前台没能交还」：窗口已经最小化，但焦点没回到你原来那个窗口（全屏独占挡 SetForegroundWindow 就会这样），发我这段日志"
}
if ($fallbackMin -gt 0) {
    "· 有 $fallbackMin 次走的是兜底路径（ShowWindow 被挡 → SC_MINIMIZE 成功）：这是成功、不是失败，只说明那些窗口在挡 ShowWindow"
}
if ($diagModal -gt 0 -or $diagFgNotRestored -gt 0 -or $diagOther -gt 0) {
    "! 有 $($diagModal + $diagFgNotRestored + $diagOther) 条动作副作用诊断（见上面「动作副作用诊断」段）：这些是我们动作的副作用，不是判定错误，但要看一眼"
}
if ($giveUp -eq 0 -and $cancel -eq 0 -and $minFail -eq 0 -and $handoffFail -eq 0 -and $relapse -eq 0 -and $burstWindows.Count -eq 0 -and $totalBlocks -gt 0) {
    '✓ 没有放弃拦截、没有取消拦截、没有最小化失败、没有前台没交还 —— 拦得干净'
}
if (-not $VerboseLog -and $heartbeats -eq 0) {
    ''
    '提示：这次是普通日志，能看"拦了谁/放行了什么"，但看不到每次判定的完整依据。'
    '     想深挖某一局，用 --verbose 启动守卫再打一局，然后跑 pwsh ./verify-real.ps1 -VerboseLog'
}
