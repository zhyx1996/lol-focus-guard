# 用假客户端跑"窗口化"测试的**统一入口** —— 专治一个反复犯的操作失误
#
# 背景（今天犯过一次、前天也犯过一次）：
#   守卫是单实例的（互斥锁）。如果真实的守卫正在跑（监控 LeagueClientUx / wegame），
#   你再 `Start-Process LolFocusGuard.exe --targets=FakeClient`，**新实例会直接退出**，
#   而真实守卫根本不认识 FakeClient —— 于是日志里只有 `前台变化 → FakeClient`，
#   却一条判定都没有（既不拦也不放行），看起来像"守卫不工作"，实际是测试根本没跑起来。
#
# 这个脚本保证顺序正确：
#   1. 停掉正在跑的真实守卫（按可执行文件路径精确过滤，不按名字乱杀）
#   2. 确认没有残留实例
#   3. 起测试守卫（--targets=...）
#   4. 确认**确实是测试实例**在跑（检查日志里最新那条"守卫启动，监控进程"是我们要的名单）
#   5. 跑测试窗口，收集结论
#   6. 无论成败，最后恢复真实守卫
#
# 用法：
#   pwsh ./run-fake-test.ps1 -Mode fast       # 假客户端每 80ms 抢一次
#   pwsh ./run-fake-test.ps1 -Mode modal      # 带模态框（只抢前台、不最小化）
#   pwsh ./run-fake-test.ps1 -Mode minimized  # 保持最小化硬抢前台
#   pwsh ./run-fake-test.ps1 -Mode quiet -Seconds 20
param(
    [ValidateSet('fast', 'modal', 'minimized', 'quiet')]
    [string]$Mode = 'fast',
    [int]$Seconds = 12,
    # 不恢复真实守卫（连续跑多个测试时用，最后一个别加这个开关）
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$exe = Join-Path $PSScriptRoot 'LolFocusGuard.exe'
$fake = Join-Path $PSScriptRoot 'FakeClient.exe'
$guardLog = Join-Path $env:LOCALAPPDATA 'LolFocusGuard\guard.log'

if (-not (Test-Path $exe)) { throw "找不到 $exe，先跑 build.ps1" }
if (-not (Test-Path $fake)) { throw "找不到 $fake，先跑 build.ps1" }

function Stop-Guard {
    Get-Process LolFocusGuard -ErrorAction SilentlyContinue | ForEach-Object {
        try { if ($_.Path -eq $exe) { $_ | Stop-Process -Force } } catch { }
    }
    # 等它真的退出，再确认一次 —— 互斥锁释放有延迟，急着起新实例会直接被挡
    for ($i = 0; $i -lt 20; $i++) {
        $p = Get-Process LolFocusGuard -ErrorAction SilentlyContinue
        if (-not $p) { return }
        Start-Sleep -Milliseconds 150
    }
    throw "守卫停不掉（还有实例在跑），本轮测试不跑，免得又变成'新实例被互斥锁挡掉'"
}

# 1-2. 停真实守卫
Stop-Guard
Write-Host "已停掉正在跑的守卫"

# 3. 起测试守卫
$beforeLen = (Get-Item $guardLog).Length
$guard = Start-Process $exe -ArgumentList '--targets=FakeClient', '--verbose' -PassThru
Start-Sleep -Milliseconds 900

# 4. 确认是**测试实例**在跑：新实例必须写下我们指定的监控名单
$fresh = ''
for ($i = 0; $i -lt 20; $i++) {
    $fs = [IO.File]::Open($guardLog, 'Open', 'Read', 'ReadWrite')
    try {
        $fs.Seek($beforeLen, 'Begin') | Out-Null
        $sr = New-Object IO.StreamReader($fs)
        $fresh = $sr.ReadToEnd()
        $sr.Close()
    } finally { $fs.Close() }
    if ($fresh -match '守卫启动，监控进程: FakeClient') { break }
    Start-Sleep -Milliseconds 200
}
if ($fresh -notmatch '守卫启动，监控进程: FakeClient') {
    Write-Host "!! 测试守卫没起来（多半是单实例互斥锁还被占着）"
    Write-Host "   日志里最新的启动行："
    ($fresh -split "`r?`n" | Where-Object { $_ -match '守卫启动|监控进程' }) | ForEach-Object { "   $_" }
    if (-not $NoRestore) { pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restart-guard.ps1') | Out-Null }
    throw "本轮测试无效（不是守卫的问题，是测试实例没跑起来）"
}
Write-Host "测试守卫已就绪（--targets=FakeClient）"

# 5. 跑测试窗口
$fakeProc = Start-Process $fake -ArgumentList $Mode -PassThru
Start-Sleep -Seconds $Seconds
Get-Process -Id $fakeProc.Id -ErrorAction SilentlyContinue | Stop-Process -Force
Stop-Guard
Start-Sleep -Milliseconds 500

# 收结论
$fs = [IO.File]::Open($guardLog, 'Open', 'Read', 'ReadWrite')
try {
    $fs.Seek($beforeLen, 'Begin') | Out-Null
    $sr = New-Object IO.StreamReader($fs)
    $text = $sr.ReadToEnd()
    $sr.Close()
} finally { $fs.Close() }

$blocks = ([regex]::Matches($text, '拦截 #')).Count
$minOk = ([regex]::Matches($text, '最小化成功')).Count
$modalOnly = ([regex]::Matches($text, '模态窗口只抢前台不最小化')).Count
$selfMin = ([regex]::Matches($text, '放行原因: 它自己就是最小化状态')).Count
$recheck = ([regex]::Matches($text, '复查: 最小化窗口占着前台超过')).Count
$giveUp = ([regex]::Matches($text, '放弃拦截')).Count

Write-Host ""
Write-Host ("模式 {0}，{1} 秒：" -f $Mode, $Seconds)
Write-Host ("  拦截 {0} 次 / 最小化成功 {1} 次 / 只抢前台 {2} 次" -f $blocks, $minOk, $modalOnly)
Write-Host ("  最小化状态放行 {0} 次 / 复查交还 {1} 次 / 放弃拦截 {2} 次" -f $selfMin, $recheck, $giveUp)

# 6. 恢复真实守卫
if (-not $NoRestore) {
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'restart-guard.ps1') | Select-Object -Last 1
}
