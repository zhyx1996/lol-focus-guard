# 验证真实判定链（不依赖输入注入，ACE 拦截了 SendInput 也能跑）：
#   阶段 1：客户端进程的窗口"第一次"出现（登录页/启动页）→ 放行；之后同一窗口再自弹 → 拦截
#   阶段 2：客户端进程在跑的时候，WeGame 的窗口自弹 → 拦截
#   阶段 3：客户端进程没跑的时候，WeGame 的窗口自弹 → 放行（国服登录面板）
#
# 做法：把 FakeClient.exe 复制成 LeagueClientUx.exe / wegame.exe（进程名就是判定的输入），
# 它们会自己最小化→自己抢前台，正好模拟"匹配成功弹窗"。
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc /nologo /target:winexe /optimize+ /out:FakeClient.exe /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll FakeClient.cs | Out-Null

$tmp = Join-Path $env:TEMP 'lfg-rules'
New-Item -ItemType Directory -Force -Path $tmp | Out-Null
Copy-Item (Join-Path $root 'FakeClient.exe') (Join-Path $tmp 'RiotClientServices.exe') -Force
Copy-Item (Join-Path $root 'FakeClient.exe') (Join-Path $tmp 'wegame.exe') -Force

$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'
$fail = 0
# 只停"我们自己造出来的"进程（按可执行文件路径过滤），绝不按进程名批量杀 ——
# 这台机器上真的可能开着 wegame.exe / RiotClientServices.exe，误杀就是把用户正在用的东西关了。
function Stop-Mine($names) {
    Get-Process $names -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
}
# 真机上真的可能开着 wegame.exe / RiotClientServices.exe：只要发现"不是我们造的"同名进程，
# 就跳过本测试 —— 因为守卫是按进程名匹配目标的，跑下去会把用户真实的 WeGame/客户端当成靶子。
$foreign = Get-Process RiotClientServices, wegame -ErrorAction SilentlyContinue |
    Where-Object { -not $_.Path -or -not $_.Path.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase) }
if ($foreign) {
    "跳过：这台机器上正开着真实的 " + (($foreign | ForEach-Object { $_.ProcessName }) -join ', ') + "，跑下去会误伤，不测这条链。"
    '结果: SKIP'
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    exit 0
}
Stop-Mine @('LolFocusGuard', 'RiotClientServices', 'wegame')
Start-Sleep -Milliseconds 600
# 下面三段断言都是"整份日志里出现过 >=1 次"：不清空日志的话，上一轮的旧日志就能让本轮假通过。
Remove-Item $guardLog -ErrorAction SilentlyContinue


$client = Start-Process (Join-Path $tmp 'RiotClientServices.exe') -PassThru
Start-Sleep -Seconds 1
$guard = Start-Process (Join-Path $root 'LolFocusGuard.exe') -ArgumentList '--targets=RiotClientServices,wegame','--verbose' -PassThru
Start-Sleep -Milliseconds 700
if ($guard.HasExited) { '跳过：守卫启动即退出（单实例互斥锁可能还被上一轮实例占着），本轮结论无效。'; '结果: SKIP'; exit 0 }
Start-Sleep -Seconds 8

$text = Get-Content $guardLog -Raw
$firstAllow = ([regex]::Matches($text, '放行原因: 客户端窗口第一次出现')).Count
$blocked1 = ([regex]::Matches($text, '拦截 #\d+：RiotClientServices')).Count
"阶段 1：客户端窗口第一次出现放行 $firstAllow 次，之后拦截 $blocked1 次"
if ($firstAllow -ge 1 -and $blocked1 -ge 1) { '阶段 1: PASS（第一次放行、后续拦截）' }
elseif ($firstAllow -eq 0) { '阶段 1: FAIL（第一次出现没放行，登录页会被拦）'; $fail++ }
else { '阶段 1: FAIL（后续自弹没拦住）'; $fail++ }

# ---- 阶段 2：客户端刚起来（启动宽限期内）+ WeGame 自弹 → 应该**放行** ----
# 为什么改：原来这里断言"应该拦"，但守卫有一条 `WeGameLaunchGraceMs = 20000` 的启动宽限
# （真机依据：你用 WeGame 启动 LoL 后，WeGame 自己会把窗口递上来，不放行就会把它缩掉两次）。
# 本阶段的时间线只有 ~10 秒，正好落在宽限里，所以守卫放行是**对的**，是测试预期过时了。
# 现在改成同时验两条：宽限期内放行（这里），宽限期外拦截（阶段 2b）。
$wg = Start-Process (Join-Path $tmp 'wegame.exe') -PassThru
Start-Sleep -Seconds 6
$part2 = (Get-Content $guardLog -Raw)
$allowGrace = ([regex]::Matches($part2, '放行原因: WeGame 启动流程')).Count
$blockWg = ([regex]::Matches($part2, '拦截 #\d+：wegame')).Count
"阶段 2：启动宽限期内 WeGame 自弹 —— 放行 $allowGrace 次，拦截 $blockWg 次"
if ($allowGrace -ge 1 -and $blockWg -eq 0) {
    '阶段 2: PASS（启动宽限期内放行 WeGame 的卡片，一次没误拦）'
} elseif ($blockWg -ge 1) {
    '阶段 2: FAIL（宽限期内就把 WeGame 卡片拦了，真机上会把启动流程缩掉）'; $fail++
} else {
    '阶段 2: FAIL（既没放行也没拦截 —— WeGame 窗口根本没抢到前台？）'; $fail++
}

# ---- 阶段 2b：过了启动宽限期，客户端还在跑 + WeGame 自弹 → 应该拦 ----
# 等宽限过期：守卫是从"客户端第一次被检测到"开始计 20 秒，这里补足剩余时间。
Start-Sleep -Seconds 12
$wgB = Start-Process (Join-Path $tmp 'wegame.exe') -PassThru
Start-Sleep -Seconds 6
$part2b = (Get-Content $guardLog -Raw)
$blockWgB = ([regex]::Matches($part2b, '拦截 #\d+：wegame')).Count
"阶段 2b：宽限期过后 WeGame 自弹被拦 $blockWgB 次"
if ($blockWgB -ge 1) {
    '阶段 2b: PASS（宽限期一过，WeGame 再抢前台就拦）'
} else {
    $graceLine = @($part2b -split "`r?`n" | Where-Object { $_ -match 'wegame' } | Select-Object -Last 3) -join "`n"
    "阶段 2b: FAIL（宽限期过了还没拦，真机上就是卡片一直抢前台）`n$graceLine"; $fail++
}
Stop-Process -Id $wgB.Id -Force -ErrorAction SilentlyContinue


# ---- 阶段 3：客户端没跑 + WeGame 自弹 → 应该放行 ----
Stop-Process -Id $client.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $wg.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
$wg2 = Start-Process (Join-Path $tmp 'wegame.exe') -PassThru
Start-Sleep -Seconds 8
$part3 = Get-Content $guardLog -Raw
$allowWg = ([regex]::Matches($part3, '放行原因: WeGame 登录阶段|放行 WeGame 窗口（登录阶段')).Count
"阶段 3：客户端不在时 WeGame 窗口放行 $allowWg 次"
if ($allowWg -ge 1) { '阶段 3: PASS（登录阶段 WeGame 放行）' } else { '阶段 3: FAIL（登录面板会被拦）'; $fail++ }

Stop-Process -Id $guard.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $wg2.Id -Force -ErrorAction SilentlyContinue
Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
if ($fail -eq 0) { '结果: PASS' } else { '结果: FAIL（' + $fail + ' 个阶段失败）'; exit 1 }
