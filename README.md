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

## 放行规则（为什么登录页不会被拦）

「自己弹出来」也分情况，下面几条按顺序判断，命中就放行：

1. **你点的**：鼠标按下那一刻，指针落在目标窗口上；或者落在任务栏 / 开始菜单 / 桌面上。
2. **Alt+Tab**：按着 `Alt`。
3. **WeGame 登录阶段**：客户端进程（`LeagueClientUx`）还没起来的时候，WeGame 的窗口一律放行。
   国服的账号登录窗口是 **WeGame** 弹的（进程 `wegame.exe`、窗口类 `TWINCONTROL`、
   标题 `WeGame`），拦了它你就连"点击登录"都点不了。
4. **客户端还没登录**：查客户端自己的本机接口（LCU，只连 `127.0.0.1`）——
   `/lol-summoner/v1/current-summoner` 返回 **404** 就是没登录，这时客户端的窗口
   （登录页、安全检测、启动页）一律放行；返回 **200** 已登录，正常拦。
5. **回退规则**：上面那条查不到时（客户端没跑 / 读不到 `lockfile`），退回到
   "进程启动后 5 分钟内**第一次出现的**窗口"放行。

> **为什么不能按标题区分？** 因为登录页和登录后的客户端是**同一个窗口**：同一个 hwnd、
> 窗口类 `RCLIENT`、标题 `League of Legends`。客户端日志里 `Window 1` 从
> `now visible` → `now minimized` → `now restored` 一直是同一个，中途只是换了页面。
> 所以只能靠"登录了没有"这种**外部状态**（LCU 接口）来判断，不能靠标题/类名。

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
pwsh ./test-fake-client.ps1      # 假客户端反复弹前台，验证"弹一次拦一次"
pwsh ./test-live.ps1             # 需要真实客户端在运行
```

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
