# 编译脚本：生成 LolFocusGuard.exe（主程序）和 FakeClient.exe（测试用的假客户端）
# 只依赖系统自带的 .NET Framework 4.x 的 csc.exe，不需要装 SDK / Visual Studio。
param(
    # 不结束正在运行的守卫（CI 里用）
    [switch]$SkipStop
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '找不到 csc.exe，需要 .NET Framework 4.x'
}

if (-not $SkipStop) {
    # exe 正在运行的话编译会报 CS0016（文件被占用）
    Get-Process LolFocusGuard -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

& $csc /nologo /target:winexe /optimize+ /out:LolFocusGuard.exe `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    LolFocusGuard.cs
if ($LASTEXITCODE -ne 0) { throw "编译 LolFocusGuard 失败: $LASTEXITCODE" }

& $csc /nologo /target:winexe /optimize+ /out:FakeClient.exe `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    FakeClient.cs
if ($LASTEXITCODE -ne 0) { throw "编译 FakeClient 失败: $LASTEXITCODE" }

Write-Host '编译完成: LolFocusGuard.exe, FakeClient.exe'
