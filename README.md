# LOL 焦点守卫 · LolFocusGuard

阻止英雄联盟客户端和 WeGame 自动抢焦点，同时允许用户主动点击、切换和操作它们。

独立 Win32 窗口工具：不注入客户端、不读取客户端内存、不改客户端文件、不安装驱动。兼容 .NET Framework 4.x，使用系统自带 csc.exe 构建。

## 使用

运行 LolFocusGuard.exe，托盘右键可暂停、恢复、打开日志和退出。Ctrl+Alt+F9 暂停五分钟或恢复。开机自启可把快捷方式放入 `shell:startup`。

- **主动切过去**：点击客户端、任务栏、缩略图、托盘、开始菜单，或使用 Alt+Tab / Win+数字等实际切换操作时放行。
- **正在使用**：保持前台期间不会因为三秒保护期到期而被追打；同进程拥有的对话框可以正常打开。
- **切到别处**：立即撤销刚才的授权；客户端随后自动抢焦点，重新判定并打回。
- **正在打游戏**：游戏中的点击、QWER 或 Alt 自施法都不能授权客户端抢焦点。真正的 `League of Legends.exe` 永远不作为拦截目标。
- **游戏退出**：上一个已认可的窗口关闭时，允许客户端自然接管前台。

恢复目标是最近一次已认可的前台窗口，也可以是用户正在使用的 WeGame 或客户端。只操作自动抢前台的目标，不会盲目切回更早的程序。

## 边界

本工具在 Windows 已经切换焦点后恢复，可能短暂闪现。轮询降级路径最多等待 30ms 收集晚到输入，再执行窗口动作；这不是端到端延迟保证。独占全屏可能已经发生最小化或画面切换，需要真机验证。

“强制聚焦”依据输入和窗口事件推断。任务栏入口通常无法精确获得最终选中的 HWND，因此给下一次实际应用激活一张一次性凭据：Shell 中间窗口不消费；浏览器等普通程序同样会消费。偶合时序仍可能漏拦一次，不能宣称百分之百识别。

低级输入钩子不可用时，15ms 的 GetAsyncKeyState 采样继续工作，但极短点击可能漏采。鼠标位置不可判定但确实采到按下时，优先避免误拦，只授权当前一次激活，不延长后续免拦期。

启动和登录兼容：WeGame 在客户端进程未启动时放行；客户端进程刚启动的第一个窗口放行一次。解锁后保留两秒系统恢复宽限。这些放行不生成用户输入凭据。LCU 状态仅用于诊断，不参与判定。

## 配置与命令行

`targets.txt` 一行一个进程名、不带 .exe，# 开头为注释；修改后重启生效。默认包括 LeagueClientUx、LeagueClient、RiotClientServices、wegame、wegame_env。启用 WeGame 监控时一并识别其 browser/pallas 辅助窗口；QQ 同名 browser 不匹配。

| 参数 | 行为 |
|---|---|
| --targets=a,b | 覆盖进程名单；不包含 WeGame 时不会额外启用其辅助窗口识别 |
| --verbose | 详细记录前台变化、放行原因和取消请求 |
| --lcu-dir=路径 | 提供 lockfile 目录，仅诊断 |
| --no-click-allow | 测试模式，忽略输入、登录和启动放行；真正的对局窗口仍豁免 |
| --report | 汇总已有日志；可用 --log=路径 指定文件 |

`lcu-dir.txt` 也可以提供安装目录。不需要新增配置或迁移现有配置。

日志默认保存在 `%LOCALAPPDATA%\LolFocusGuard\guard.log`，后台批量写入，超过 8MB 轮转为 guard.log.1。保留客户端启停小结、半小时心跳和托盘累计拦截数。只有实际发出窗口动作才记“拦截 #N”；取消的请求不累计失败次数。

## 构建和验证

```powershell
# 独立输出，不结束已有守卫、不覆盖正在运行的文件
pwsh ./build.ps1 -SkipStop -OutputDirectory ./artifacts
./artifacts/LogicTest.exe
./artifacts/StateTest.exe
./artifacts/DesktopTest.exe --smoke

# 会使用测试窗口和模拟点击；已有守卫或对局正在运行时跳过
pwsh ./test-desktop.ps1 -Interactive
```

`build.ps1` 默认输出到仓库目录；仅在该模式且未指定 -SkipStop 时停止**同路径**的守卫。生成守卫、FakeClient、LogicTest、StateTest 和 DesktopTest。

- LogicTest：纯规则、键盘切换序列和进程分类。
- StateTest：注入时钟、窗口观察和动作接口，验证完整事件序列。覆盖晚到点击、窗口身份复用、任务栏切到其他程序、用户接管、暂停/退出、每一步动作取消、防拉锯和系统 tick 回绕。
- DesktopTest --smoke：只读验证真实 Win32 身份映射和守卫生命周期，不发送窗口动作，不影响已有守卫。
- DesktopTest --interactive：自建私有测试进程和日志，断言实际前台 HWND、最小化状态、完整日志区间与计数；只清理自己创建的进程。退出码 0=PASS、1=FAIL、2=SKIP。输入注入失败不能当作通过。
- test-input-rule、test-user-intent、test-restore 是新桌面测试的兼容入口，需要 -Interactive。历史 test-live 等脚本仅供人工调试，不作为重构后的验收依据。
- test-click-late、test-session、test-rules 改为确定性规则/序列测试入口，不锁屏、不发送真实输入。test-fake-client 与 test-cursor-covered 转发桌面测试。

CI 构建后运行 LogicTest 和 StateTest。真实客户端仍需人工验收：登录、启动对局、对局结束、正常与独占全屏，以及反复主动切进切出客户端。未完成真机验收前，不应把自动化测试结果等同于所有游戏环境的保证。

接续 dsh 新增的 `verify-real.ps1`：打一局后执行 `pwsh ./verify-real.ps1 -VerboseLog`，只读汇总日志中的拦截、主动放行、反复抢前台和潜在漏网窗口。重构版保留其前台、心跳、起退日志格式，并保留模态窗口诊断。日志推断仍需对照实际操作，不能单凭脚本判定完成真机验收。

dsh 在重构前版本的真机记录：WeGame 启动 50 秒内出现八次前台变化，登录阶段全部放行，零次误拦。本次保留该登录兼容行为；这条历史记录不代表新状态机已经通过真机对局验收。

## 实现

`FocusEngine.cs` 是串行协调器，所有输入、前台变化、授权、暂停、重试和动作结果共用一个状态锁。输入快照包含序号、单调时间、坐标、窗口身份、来源和入口类型；钩子与轮询对同一次按下去重。

窗口身份包含 HWND、PID、线程、进程创建时间和窗口世代。动作请求带前台切换序号与授权版本；每次最小化、发送最小化消息或恢复焦点前重新校验，用户接管和暂停立即使旧请求失效。等待不占状态锁。最小化使用异步窗口调用，保留 WM_SYSCOMMAND 兜底。

`Guard.cs` 负责 Win32 采集、后台线程和异步轮转日志，`Rules.cs` 保留不访问系统的纯决策函数。`LolFocusGuard.cs` 保留原生声明、托盘、热键、会话通知、命令行和日志报告。

MIT，见 LICENSE。
