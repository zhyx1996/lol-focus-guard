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
| `--lcu-dir=<路径>` | 客户端安装目录（用来找 `lockfile`，只写日志，不参与判定） |
| `--no-click-allow` | 关掉所有「你在操作」的放行（测试用，等于什么都拦） |
| `--verbose` | 每次前台变化都写日志（含窗口类名/标题/尺寸/当次按键落在谁身上，排查问题用） |
| `--report` | 不开守卫，只把已有日志汇总成一份短报告（`%LOCALAPPDATA%\LolFocusGuard\report.txt`） |

出问题的时候这样取证，比发整份日志方便：

```powershell
.\LolFocusGuard.exe --report
notepad $env:LOCALAPPDATA\LolFocusGuard\report.txt
```

报告长这样：

```
时间范围: 2026-10-28 01:00:25 → 01:00:36
拦截: 5 次
  FakeClient / "FakeClient" ×5
放行: 21 次
  正在放弃拦截期 ×21
放弃拦截（防拉锯，10 秒内拦 6 次就停手）: 1 次
判为自弹的依据:「350ms 内没有鼠标按键按下」5 次，「按键落在别的程序上」0 次
```

### `lcu-dir.txt` — 登录状态检测

一行客户端安装目录（里面应该有 `lockfile`），例如：

```
D:\WeGameApps\英雄联盟\LeagueClient
```

不写也行：程序会自己从客户端进程目录、`C:\Riot Games\League of Legends`、
`D:\WeGameApps\英雄联盟\LeagueClient` 里猜。

## 核心判据：区分「你自己切的」和「它自己弹的」

关键不是猜标题、也不是猜登录状态，而是**那一刻你有没有真的动鼠标**。判定依据是硬件级的两个事实：

- **鼠标按键是不是刚刚真的按下了** —— `GetAsyncKeyState(VK_LBUTTON/VK_RBUTTON)` 每 15ms 采一次，
  抓「按下」这一瞬间。它读的是硬件状态，**不是钩子**，ACE 掐不掉；
- **按下的那一刻指针落在谁身上** —— `GetCursorPos()` → `WindowFromPoint()` → `GetAncestor(GA_ROOT)` → 进程名。

| 情况 | 判定 |
|---|---|
| 最近 350ms 内**没有任何鼠标按键按下**，窗口却跑到前台了 | **拦截**（它自己弹的） |
| 按键按下，且那一刻指针在**被切到前台的这个窗口**上 | 放行（你点的） |
| 按键按下，且那一刻指针在**任务栏 / 开始菜单 / 桌面**上 | 放行（你点的任务栏按钮/缩略图/通知） |
| 按键按下，但落在**别的程序**上（你在打游戏、打字、点别处） | **拦截**（你点的是别的窗口，它没理由被抬起） |
| 1.5 秒内按过 `Alt` / `Win`（Alt+Tab、Win+数字、Win+D…） | 放行 |
| 上一个前台窗口**已经关闭**（刚打完这局、游戏窗口退出） | 放行（前台自然接管，不该"刚结束就被最小化"） |

> **为什么"最近有没有输入"不够**：鼠标随手一动就算输入了。第一版就是拿「`GetLastInputInfo` 输入年龄 ≤ 500ms
> + 指针位置」判的，真机上被客户端将了一军：匹配成功弹到全屏时窗口正好盖在指针下面，
> 「指针在它上面」被当成「你点的」—— 一局 10 次弹窗全放行。改成「物理按键按下」之后，
> **鼠标只是躺着不动、或者只是划过去，都不再算数**：自弹没有按键，手动切一定有按键。
> （`test-cursor-covered.ps1` 专门复现"窗口盖住指针"这个场景。）
> 同理，**重试轮**（窗口赖在前台 1.2s 后再判一次）里指针必然压在它身上，所以那一轮
> 不把"指针在它上面"算成你点的。

再加一条**防拉锯阀**：同一个窗口 10 秒内被拦超过 5 次（比如它是个一直往回抢的模态框），
就主动放弃拦截 60 秒，并在日志里记一笔。宁可让它弹一次，也不能跟它来回拉锯把机器卡成幻灯片。

> **为什么不拿键盘当放行理由**：打游戏时你一直在按 QWER，要是按键就算「你在操作」，
> 那客户端趁你操作时弹出来就永远拦不住了。所以键盘只认 `Alt`/`Win` 这两个「切窗口专用键」。
>
> **低级钩子（`WH_MOUSE_LL` / `WH_KEYBOARD_LL`）只是锦上添花**：钩子活着时能拿到更细的输入流。
> 但实测**WeGame 跑起来之后（它自带的 ACE / SGuard 安全模块）会把别的进程的低级输入钩子悄悄掐掉**
> ——钩子回调一次都不来，且不报错。所以判定主依据是上面两条系统级事实，钩子挂了也照样工作。

再加两条针对启动阶段的放行：

1. **客户端窗口第一次出现**（`LeagueClientUx` / `LeagueClient` / `RiotClientServices` 的窗口第一次冒出来）
   → 放行。登录页、启动页、安全检测都在这个阶段，一个客户端实例只放行这一次。
2. **WeGame 登录阶段**：客户端进程还没起来时，`wegame.exe` / `wegame_env.exe` 的窗口一律放行。
   国服的账号登录/启动面板是 WeGame 弹的（窗口类 `TWINCONTROL`），拦了它你连"点击登录"都点不了。

> **WeGame 的窗口不一定属于 `wegame.exe`**：真机日志显示它的界面跑在
> `browser.exe`（窗口类 `CefTopWindow`、标题 `WeGame`）和 `pallas.exe`（窗口类 `TWINCONTROL`、
> `SIDE_WEB_VIEW`）里。只按进程名认目标，"游戏进程已启动"那张卡片、WeGame 的侧边网页
> 这些抢前台会整条漏过去。所以判据是**进程名 + 窗口标题/类名**一起看，
> 并且特意排除了 QQ 那个同名的 `browser.exe`（窗口类 `QBCoreAx`、无标题）。
> 这套分类是纯字符串比较，`LogicTest.exe` 里有 13 条对照用例。

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

## 已知局限

- **会有一次极短的闪现（≈9ms）**，做不到完全不出现，原因见上。
- 如果你**主动**点客户端却被拦了（点击没被识别成"你点的"），再点一次即可，
  或者 `Ctrl+Alt+F9` 暂停。
- WeGame 的登录窗口和"游戏进程已启动"卡片同属 `wegame.exe` 那一套界面，
  目前靠"客户端进程起没起来"区分（登录阶段全放行）。极端时序下（卡片先弹、客户端后起）
  卡片可能被放行一次。
- EXCLUSIVE 全屏游戏下的观感需要在真打一局时确认。
- **英雄联盟的窗口会挡掉别的进程的 `ShowWindow`**：实测它的登录窗口
  （`Client.exe` / `TWINCONTROL`）`ShowWindow(SW_MINIMIZE)` 直接返回 `False`、窗口纹丝不动，
  `SetWindowPos` 也一样；但它认 `WM_SYSCOMMAND / SC_MINIMIZE`——`PostMessage` 过去立刻最小化。
  拦下的动作里已经加了这条兜底（先 `ShowWindow`，验 `IsIconic` 没成功就换消息），
  日志里会写「ShowWindow 被挡，改用 WM_SYSCOMMAND/SC_MINIMIZE 最小化成功」。
- 没有可以交还的窗口时（比如上一个前台窗口本身也在监控名单里），光"不还焦点"等于没动作，
  所以这种情况直接最小化，由系统自己挑下一个前台窗口。

## 测试

```powershell
pwsh ./build.ps1
.\LogicTest.exe                  # 规则表 + 窗口分类的穷举测试（34 条，不开窗口、不注入输入，纯逻辑）
pwsh ./test-fake-client.ps1      # 假客户端反复自己弹前台 → 必须"弹一次拦一次"
pwsh ./test-input-rule.ps1       # 核心判据：模拟点击目标窗口=放行；无输入自弹=拦截
pwsh ./test-rules.ps1            # 真实判定链：客户端窗口首次出现放行→后续拦截、WeGame 卡片拦截/登录放行
pwsh ./test-cursor-covered.ps1   # 弹窗正好盖住鼠标指针时必须照样拦（真机上漏拦的那个场景）
pwsh ./test-restore.ps1          # "打回"这个动作本身：前台是否真的交还、窗口是否真被最小化
pwsh ./test-live.ps1             # 需要真实客户端在运行（用真窗口再验一遍）
```

判定规则本身在 `Rules.cs` 里是一个**纯函数**（`FocusRules.Decide(DecisionFacts)`）：
不碰窗口、不读全局状态，只吃"事实"、吐"放行/拦截 + 原因"。`LogicTest.cs` 直接构造事实喂给它，
把每条规则和边界（350ms / 1500ms 宽限、优先级顺序）都验一遍。改规则前后跑一次，
就知道有没有把「你手动切窗口」改成「拦」，不用真开客户端去试：

```
> .\LogicTest.exe
  ok   没有任何输入，客户端自己跳前台 → 拦截
  ok   按键落在别的程序上（你在打游戏）→ 拦截
  ok   按键落在客户端窗口上 → 放行
  ...
结果: PASS，通过 17，失败 0
```

`test-input-rule.ps1` 每次现场编译一个 200 行的测试小程序（`PopTarget.cs`），
开两个窗口：`PopTarget`（被监控目标）和 `BgWin`（对照组），然后用 `SendInput`
真的点一下 `PopTarget`——低级鼠标钩子连 `SendInput` 造的事件也看得见，
所以这条路径和真人点击走的是同一段代码。

### 实测记录

| 场景 | 结果 |
|---|---|
| 假客户端（`FakeClient.exe`）无输入自弹前台 | 前 5 次全拦（其中 5 次识别为"从最小化弹出"），第 6 次触发防拉锯阀放弃 60 秒 |
| `PopTarget` 被 `SendInput` 真点击（有按键、指针在它身上） | 放行，不拦（注入被 ACE 拦掉时这一项跳过） |
| `PopTarget` 无输入自抢前台 | 拦截，并把前台交还原窗口 |
| 客户端进程（假 `RiotClientServices.exe`）窗口第一次出现 | 放行；之后同一窗口再自弹 → 拦截 |
| 客户端在跑时 `wegame.exe`（假）自弹前台 | 拦截 |
| 客户端没在跑时 `wegame.exe`（假）自弹前台 | 放行（登录阶段） |
| WeGame 真机启动抢前台（客户端还没起来） | 放行（真机日志验证） |
| `GetLastInputInfo` 对 `SendInput` 的反应 | 点击后 age=0ms，200ms 后 219ms（可用） |
| 真打一局验证匹配成功弹窗 | 需要一局真实对局，未在开发机复现 |

> **注意**：WeGame 的 ACE 有时连 `SendInput` / `SetCursorPos` 一起拦（返回 False、错误码 0、
> 指针不动）。这时 `test-input-rule.ps1` 的 A 阶段（模拟点击）测不了，脚本会打印提示并跳过，
> 不算失败。

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
