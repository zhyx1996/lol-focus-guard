# 编译脚本：生成 LolFocusGuard.exe（主程序）和 FakeClient.exe（测试用的假客户端）
# 只依赖系统自带的 .NET Framework 4.x 的 csc.exe，不需要装 SDK / Visual Studio。
param(
    # 不结束正在运行的守卫（CI 里用）
    [switch]$SkipStop,
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '找不到 csc.exe，需要 .NET Framework 4.x'
}

if (-not $SkipStop -and $OutputDirectory -eq $PSScriptRoot) {
    # exe 正在运行的话编译会报 CS0016（文件被占用）
    Get-Process LolFocusGuard -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Join-Path $OutputDirectory 'LolFocusGuard.exe') } | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

& $csc /nologo /codepage:65001 /target:winexe /optimize+ ("/out:" + (Join-Path $OutputDirectory 'LolFocusGuard.exe')) `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    LolFocusGuard.cs Guard.cs FocusEngine.cs Rules.cs
if ($LASTEXITCODE -ne 0) { throw "编译 LolFocusGuard 失败: $LASTEXITCODE" }

& $csc /nologo /codepage:65001 /target:winexe /optimize+ ("/out:" + (Join-Path $OutputDirectory 'FakeClient.exe')) `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    FakeClient.cs
if ($LASTEXITCODE -ne 0) { throw "编译 FakeClient 失败: $LASTEXITCODE" }

# 规则表测试（控制台程序，不开窗口、不注入输入）
& $csc /nologo /codepage:65001 /target:exe /optimize+ ("/out:" + (Join-Path $OutputDirectory 'LogicTest.exe')) `
    /r:System.dll /r:System.Core.dll `
    LogicTest.cs Rules.cs
if ($LASTEXITCODE -ne 0) { throw "编译 LogicTest 失败: $LASTEXITCODE" }

& $csc /nologo /codepage:65001 /target:exe /optimize+ ("/out:" + (Join-Path $OutputDirectory 'StateTest.exe')) `
    /r:System.dll /r:System.Core.dll StateTest.cs FocusEngine.cs Rules.cs
if ($LASTEXITCODE -ne 0) { throw "编译 StateTest 失败: $LASTEXITCODE" }

& $csc /nologo /codepage:65001 /target:exe /optimize+ /main:DesktopTest ("/out:" + (Join-Path $OutputDirectory 'DesktopTest.exe')) `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    DesktopTest.cs LolFocusGuard.cs Guard.cs FocusEngine.cs Rules.cs
if ($LASTEXITCODE -ne 0) { throw "编译 DesktopTest 失败: $LASTEXITCODE" }

Write-Host "编译完成: $OutputDirectory（守卫、FakeClient、LogicTest、StateTest、DesktopTest）"
