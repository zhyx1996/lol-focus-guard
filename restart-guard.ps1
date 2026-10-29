# 重启守卫 —— 并且让它**脱离**当前终端/作业对象
#
# 为什么需要这个脚本：22:19:05 守卫莫名其妙退出过一次（日志最后一行就是"守卫退出"，
# 前台先是它自己的托盘菜单窗口）。当时 DSH 恰好重启，`Start-Process` 出来的守卫是
# 当前作业对象（job）的子进程，作业一关就被连带收掉 —— 用户那边就是"守卫没了，弹窗又回来了"，
# 而且完全没有提示。
#
# 用 WMI 的 Win32_Process.Create 创建进程：新进程的父进程是 WmiPrvSE，不属于本终端的作业对象，
# 终端 / DSH / 本脚本退出都不会把它带走。开机自启那条（Startup 快捷方式）本来就是独立的，
# 这个脚本是给"手工重启 / 排查时重启"用的。
#
# 用法：
#   pwsh ./restart-guard.ps1                  # 默认 --verbose
#   pwsh ./restart-guard.ps1 -NoVerbose       # 普通模式（日志少，CPU 略低）
#   pwsh ./restart-guard.ps1 -Args '--targets=FakeClient','--verbose'
param(
    [switch]$NoVerbose,
    [string[]]$Args = $null
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$exe = Join-Path $PSScriptRoot 'LolFocusGuard.exe'
if (-not (Test-Path $exe)) { throw "找不到 $exe，先跑 build.ps1" }

$guardArgs = if ($Args) { $Args } elseif ($NoVerbose) { @() } else { @('--verbose') }

# 单实例互斥：先停掉旧的（按可执行文件路径精确过滤，别误杀别的同名进程）
Get-Process LolFocusGuard -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        if ($_.Path -eq $exe) { $_ | Stop-Process -Force }
    } catch {
    }
}
Start-Sleep -Milliseconds 500

$cmd = '"' + $exe + '"'
foreach ($a in $guardArgs) { $cmd += ' ' + $a }
$r = ([wmiclass]'Win32_Process').Create($cmd, $PSScriptRoot)
if ($r.ReturnValue -ne 0) { throw "WMI 创建进程失败: ReturnValue=$($r.ReturnValue)" }

Start-Sleep -Seconds 2
$p = Get-Process -Id $r.ProcessId -ErrorAction SilentlyContinue
if (-not $p) { throw "守卫没起来（pid=$($r.ProcessId)）" }
$parent = (Get-CimInstance Win32_Process -Filter ("ProcessId=" + $p.Id)).ParentProcessId
Write-Host ("守卫已启动: pid={0}，父进程={1}（{2}），参数: {3}" -f `
    $p.Id, $parent, (Get-Process -Id $parent -ErrorAction SilentlyContinue).ProcessName, ($guardArgs -join ' '))
