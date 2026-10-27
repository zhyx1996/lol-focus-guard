# LOL 焦点守卫 · LolFocusGuard

[![build](https://github.com/zhyx1996/lol-focus-guard/actions/workflows/build.yml/badge.svg)](https://github.com/zhyx1996/lol-focus-guard/actions/workflows/build.yml)
[![license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%207%2B-lightgrey.svg)

拦住「英雄联盟客户端 / WeGame 在后台时，自己蹦到前台抢焦点」的独立小工具。

- 单文件、单进程，**不注入客户端、不读内存、不改客户端文件、不装驱动**
- 只用公开的 Win32 窗口 API（`SetWinEventHook` 系列）
- 从"系统广播前台切换"到"窗口被收回、焦点还给你"，实测 **≈9ms**（一帧左右）
- .NET Framework 4.x + 系统自带 `csc.exe` 即可编译，不需要 SDK / Visual Studio

---

## 它解决什么问题

客户端在后台（最小化、或被别的窗口盖住）的时候，这些场景会突然抢走焦点：

- **匹配成功**、对局结束回到车库、排队/选人阶段的提示
- 客户端内部网页层弹的活动、赛事、公告页（`/riotclient/ux-show`）
- WeGame 把游戏拉起来时弹的「游戏进程已启动，等待游戏窗口拉起」卡片

只要不是你自己点的、不是 Alt+Tab 过去的，它就会在你操作的位置被打回去，
如果是"从最小化状态弹出来的"，还会顺手再最小化回去——不会在你面前杵一块客户端。

## 它做不到什么（先看这条，免得白装）

- **做不到"完全不出现"**。抢前台这个动作发生在客户端进程内部（`ShowWindow` +
  `SetForegroundWindow`），要在它发生**之前**掐掉，只能把代码塞进客户端进程
  （全局 `WH_CBT` 钩子 / API hook / DLL 注入）。那类做法会跟 ACE 反作弊直接冲突，
  本项目**不做**。物理上能做到的极限是"马上收回去"，也就是那 9ms 的闪现。
- 拦的是**窗口跳前台**这件事。客户端网页层的弹窗（活动浮窗之类）如果不产生独立窗口，
  本工具管不着——那是 Sona / Pengu Loader 这类 JS 层补丁的活。
- 全屏游戏里的表现要自己实测；EXCLUSIVE 全屏下系统的前台切换行为和非全屏不一样。

## 快速开始

1. 到 [Releases](../../releases) 下载 `LolFocusGuard.exe`；
2. 丢进任意文件夹，比如 `C:\Tools\LolFocusGuard\`（同目录可以放 `targets.txt`）；
3. 双击运行。托盘出现一个蓝色盾牌图标，开始工作；
4. 开机自启（可选）：`Win+R` → `shell:startup` → 把 `LolFocusGuard.exe` 的**快捷方式**丢进去。

> 单独一个 exe 就能跑；`targets.txt`、`lcu-dir.txt` 都是可选的配置文件。

## 从源码编译

```powershell
git clone https://github.com/zhyx1996/lol-focus-guard.git
cd lol-focus-guard
pwsh ./build.ps1          # 会先结束正在运行的守卫，再编译
```

产物：`LolFocusGuard.exe`（主程序）、`FakeClient.exe`（测试用的假客户端）。

## 配置

### `targets.txt` — 监控名单

默认：`LeagueClientUx`、`RiotClientServices`、`wegame`、`wegame_env`。
一行一个进程名（不带 `.exe`），`#` 开头是注释。改完**重启程序**生效。

命令行可以覆盖：`LolFocusGuard.exe --targets=LeagueClientUx,wegame`

### 命令行参数

| 参数 | 说明 |
|---|---|
| `--targets=a,b` | 覆盖监控名单（进程名，逗号分隔） |
| `--lcu-dir=<路径>` | 客户端安装目录（用来找 `lockfile`，判断登录状态） |
| `--fresh=<秒>` | 回退放行规则的时间窗，默认 `300`，`0` = 关掉 |
| `--fresh-procs=a,b` | 参与回退放行规则的进程，默认 `LeagueClientUx,RiotClientServices` |
| `--no-click-allow` | 关掉"你点它就不拦"的放行（测试用） |
| `--verbose` | 每次前台变化都写日志（日志会涨得比较快） |

### `lcu-dir.txt` — 登录状态检测

一行客户端安装目录（里面应该有 `lockfile`），例如：

```
D:\WeGameApps\英雄联盟\LeagueClient
```

不写也行：程序会自己从客户端进程目录、`C:\Riot Games\League of Legends`、
`D:\WeGameApps\英雄联盟\LeagueClient` 里猜。

## 核心判据：区分「你自己切的」和「它自己弹的」

关键不是猜标题、也不是猜登录状态，而是**看那一刻有没有你在操作**。工具装了两个低级输入钩子
（`WH_MOUSE_LL` / `WH_KEYBOARD_LL`，回调跑在自己进程里，不注入、和 AutoHotkey 同级），
于是每一次前台切换都能对上"这之前 500ms 内你在干什么"：

| 情况 | 判定 |
|---|---|
| 500ms 内有**键盘输入**（Alt+Tab、Win+数字、点完再按…） | 放行 |
| 500ms 内有**鼠标按下**，且按下的窗口就是被切到前台的这个窗口 | 放行（你点的） |
| 500ms 内有**鼠标按下**，且按在**任务栏 / 开始菜单 / 桌面**上 | 放行（你点的任务栏按钮/缩略图） |
| 500ms 内鼠标按在**别的程序**上，这个窗口却跑到前台来了 | **拦截**（你点的是别的窗口，它没理由被抬起） |
| 完全没有任何输入，窗口突然到前台 | **拦截** |
| `Alt` 按着 | 放行 |

再加两条针对启动阶段的放行：

1. **客户端窗口第一次出现**（`LeagueClientUx` / `LeagueClient` / `RiotClientServices` 的窗口第一次冒出来）
   → 放行。登录页、启动页、安全检测都在这个阶段，一个客户端实例只放行这一次。
2. **WeGame 登录阶段**：客户端进程还没起来时，`wegame.exe` / `wegame_env.exe` 的窗口一律放行。
   国服的账号登录/启动面板是 WeGame 弹的（窗口类 `TWINCONTROL`），拦了它你连"点击登录"都点不了。

> **为什么不按标题/类名判断**：登录页和登录后的客户端是**同一个窗口**——同一个 hwnd、
> 窗口类 `RCLIENT`、标题 `League of Legends`（客户端日志里 `Window 1` 从 `now visible`
> 到 `now restored` 一直是同一个，中途只换页面）。所以标题这条路走不通。
>
> **为什么不用"登录状态"判断**：本来打算读客户端本机接口（LCU，`lockfile` 里的端口+口令），
> 但实测腾讯版客户端的 `lockfile` **全程是 0 字节空文件**（登录前后都是），这条路作废。
> 现在代码里只剩一段只写日志的探测，不参与判定。


## 日志

`%LOCALAPPDATA%\LolFocusGuard\guard.log`，托盘右键 → 「打开日志」也能直接打开。

每条拦截长这样：

```
[2026-10-27 21:38:28.532] 拦截 #1：wegame (pid=26752) 自己弹到前台（刚才最小化=否，class=TWINCONTROL，
                          标题="WeGame"，尺寸=862x486@(422,266)，事件延迟=0ms）→ 打回
```

`事件延迟` 是"系统发出前台切换事件"到"我们收到"的毫秒数（正常 0–2ms）。

## 操作

| 动作 | 方式 |
|---|---|
| 暂停 5 分钟 / 恢复 | `Ctrl+Alt+F9`，或托盘右键 |
| 看拦了多少次 | 托盘右键 →「已拦截: N 次」 |
| 看日志 | 托盘右键 →「打开日志」（或双击图标） |
| 退出 | 托盘右键 →「退出」 |

## 卸载

1. 托盘右键 → 退出；
2. 删掉启动快捷方式：`%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\` 里的 `LOL 焦点守卫.lnk`；
3. 删掉程序文件夹。

## 已知局限

- **会有一次极短的闪现（≈9ms）**，做不到完全不出现，原因见上。
- 如果你**主动**点客户端却被拦了（点击没被识别成"你点的"），再点一次即可；
  或者 `Ctrl+Alt+F9` 暂停。
- WeGame 的登录窗口和"游戏进程已启动"卡片同属 `wegame.exe`，
  目前靠"客户端进程起没起来"区分（登录阶段全放行）。极端时序下（卡片先弹、客户端后起）
  卡片可能被放行一次。
- EXCLUSIVE 全屏游戏下的观感需要在真打一局时确认。

## 测试

```powershell
pwsh ./build.ps1
pwsh ./test-fake-client.ps1      # 假客户端反复自己弹前台 → 必须"弹一次拦一次"
pwsh ./test-input-rule.ps1       # 核心判据：模拟点击目标窗口=放行；无输入自弹=拦截
pwsh ./test-live.ps1             # 需要真实客户端在运行（用真窗口再验一遍）
```

`test-input-rule.ps1` 每次现场编译一个 200 行的测试小程序（`PopTarget.cs`），
开两个窗口：`PopTarget`（被监控目标）和 `BgWin`（对照组），然后用 `SendInput`
真的点一下 `PopTarget`——低级鼠标钩子连 `SendInput` 造的事件也看得见，
所以这条路径和真人点击走的是同一段代码。

### 实测记录

| 场景 | 结果 |
|---|---|
| 假客户端（`FakeClient.exe`）无输入自弹前台 ×9 | 9 次全拦，识别"从最小化弹出"9 次 |
| `PopTarget` 被 `SendInput` 点击（有输入） | 放行，不拦 |
| `PopTarget` 无输入自抢前台 | 拦截，并把前台交还原窗口 |
| WeGame 登录面板（客户端没起来） | 放行（真机日志验证） |
| 真打一局验证匹配成功弹窗 | 需要一局真实对局，未在开发机复现 |

> **DPI 缩放**：程序启动时会调 `SetProcessDPIAware()`。不调用的话，125%/150% 缩放下
> 低级钩子给的**物理坐标**和 `WindowFromPoint` 要的**逻辑坐标**相差 1.25/1.5 倍，
> "鼠标点在了哪个窗口上"就会算错，把你自己点的窗口误判成"它自己弹的"。

## 原理（技术细节）

**三层，按顺序**：

1. **事件**：`SetWinEventHook(EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT)` ——
   系统级"前台窗口变了"的广播。回调里只判断、不做动作，耗时几十微秒。
2. **兜底**：独立线程每 50ms 读一次 `GetForegroundWindow()`，防止钩子漏事件。
3. **动作**（专用最高优先级线程，不占钩子线程）：如果是最小化状态弹出来的，
   `ShowWindow(SW_MINIMIZE)` 收回去；然后把前台交还给你原来的窗口
   （`AttachThreadInput` + `SetForegroundWindow`，不行再退一步 `SwitchToThisWindow`）。

**为什么这么快**：进程名用 `QueryFullProcessImageName` + pid 缓存（比
`Process.GetProcessById` 快一个数量级）；动作走专用线程而不是线程池；
最小化后只等 5ms 就检查前台是否已经交还。

**为什么不用注入**：全程只用公开的 Win32 窗口 API，不碰别人的进程空间、
不往客户端里装钩子。ACE 侧看不到任何异常行为。

## 许可

[MIT](LICENSE)
