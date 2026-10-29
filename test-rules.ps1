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
Start-Sleep -Seconds 8

$text = Get-Content $guardLog -Raw
$firstAllow = ([regex]::Matches($text, '放行原因: 客户端窗口第一次出现')).Count
$blocked1 = ([regex]::Matches($text, '拦截 #\d+：RiotClientServices')).Count
"阶段 1：客户端窗口第一次出现放行 $firstAllow 次，之后拦截 $blocked1 次"
if ($firstAllow -ge 1 -and $blocked1 -ge 1) { '阶段 1: PASS（第一次放行、后续拦截）' }
elseif ($firstAllow -eq 0) { '阶段 1: FAIL（第一次出现没放行，登录页会被拦）'; $fail++ }
else { '阶段 1: FAIL（后续自弹没拦住）'; $fail++ }

# ---- 阶段 2：客户端在跑 + WeGame 自弹 → 应该拦 ----
$wg = Start-Process (Join-Path $tmp 'wegame.exe') -PassThru
Start-Sleep -Seconds 8
$part2 = (Get-Content $guardLog -Raw)
$blockWg = ([regex]::Matches($part2, '拦截 #\d+：wegame')).Count
"阶段 2：客户端在跑时 WeGame 自弹被拦 $blockWg 次"
if ($blockWg -ge 1) { '阶段 2: PASS（客户端在跑时 WeGame 弹前台被拦）' } else { '阶段 2: FAIL（卡片没拦住）'; $fail++ }

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
