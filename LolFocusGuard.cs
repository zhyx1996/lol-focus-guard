// LolFocusGuard —— 从系统层面拦住英雄联盟客户端"自己弹到前台抢焦点"
//
// 原理：用 SetWinEventHook 监听全局前台窗口切换（EVENT_SYSTEM_FOREGROUND）。
// 当英雄联盟客户端窗口成为前台、且判定为"没人点它"时：
//   · 若它本来是最小化的，立刻再最小化回去；
//   · 把前台还给之前那个窗口（AttachThreadInput + SetForegroundWindow）。
// 全程不注入客户端、不读客户端内存、不改客户端文件。
//
// 编译：csc /target:winexe /out:LolFocusGuard.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll LolFocusGuard.cs
// 用法：双击运行（托盘图标）；Ctrl+Alt+F9 暂停/恢复 5 分钟；托盘菜单可暂停、查看次数、打开日志、退出。
// 调试：LolFocusGuard.exe --targets=mspaint,notepad   （用别的进程名测试，不碰真客户端）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LolFocusGuard
{
    internal static class Native
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod,
            WinEventDelegate proc, uint idProcess, uint idThread, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags,
            StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        // 关键：让本进程变成 DPI 感知，否则在缩放 125%/150% 的屏幕上，
        // 低级钩子给的物理坐标和 WindowFromPoint 用的逻辑坐标对不上，
        // 会算错"鼠标点在了哪个窗口上"。
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int SW_MINIMIZE = 6;
        public const uint WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020;
        public const uint GW_OWNER = 4;
        public const uint GA_ROOT = 2;
        public const int VK_MENU = 0x12;
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;

        // ---- 系统级"最后一次输入"时间（GetLastInputInfo）：不需要钩子，任何安全软件都拦不住 ----
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        public static extern uint GetTickCount();

        // ---- 低级输入钩子（WH_MOUSE_LL / WH_KEYBOARD_LL）：不注入，回调跑在我们自己线程里 ----
        public const int WH_KEYBOARD_LL = 13;
        public const int WH_MOUSE_LL = 14;
        public const int WM_LBUTTONDOWN = 0x0201;
        public const int WM_RBUTTONDOWN = 0x0204;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_XBUTTONDOWN = 0x020B;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_KEYDOWN = 0x0100;
        public const int WM_SYSKEYDOWN = 0x0104;

        public delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }

    /// <summary>核心守卫逻辑：装前台事件钩子 + 记录"谁在抢前台" + 抢回时立刻打回去。</summary>
    internal sealed class Guard : IDisposable
    {
        private const int POLL_MS = 50;
        private const int SW_MINIMIZE = Native.SW_MINIMIZE;

        private readonly HashSet<string> _targets;
        private readonly string _logPath;
        private readonly bool _verbose;
        private readonly bool _noClickAllow;
        private static readonly string OwnProcessName = Process.GetCurrentProcess().ProcessName;
        private readonly object _logLock = new object();
        private readonly Native.WinEventDelegate _callback;
        private readonly System.Threading.Thread _pollThread;
        private volatile bool _disposed;

        private IntPtr _hook = IntPtr.Zero;
        private IntPtr _targetHwnd = IntPtr.Zero;
        private IntPtr _lastForeground = IntPtr.Zero;
        private IntPtr _lastFgHwnd = IntPtr.Zero;
        private readonly object _sync = new object();

        private volatile bool _targetWasMinimized;
        private volatile bool _paused;
        private DateTime _resumeAt = DateTime.MinValue;
        private int _blockCount;
        private DateTime _lastIconicAt = DateTime.MinValue;
        // 每个目标窗口"上一次处于最小化"的时间，用来判断弹出来的那一刻它是不是从最小化状态恢复的
        private readonly Dictionary<IntPtr, DateTime> _iconicAt = new Dictionary<IntPtr, DateTime>();
        private readonly List<int> _targetPids = new List<int>();
        private int _scanTick;
        private DateTime _lastBlockRequestAt = DateTime.MinValue;
        private int _pollTicks;
        private DateTime _lastBlockAt = DateTime.MinValue;
        private IntPtr _lastBlockedHwnd = IntPtr.Zero;

        // pid → 进程名缓存（热路径里不能每 50ms 都去 new Process，那是几十毫秒的开销）
        private readonly Dictionary<int, string> _pidNames = new Dictionary<int, string>();
        private DateTime _pidNamesClearedAt = DateTime.Now;

        // 专用动作线程：比线程池调度快，延迟能压到几毫秒
        private readonly Queue<Action> _actions = new Queue<Action>();
        private readonly object _actionsLock = new object();
        private readonly System.Threading.AutoResetEvent _actionSignal = new System.Threading.AutoResetEvent(false);
        private readonly System.Threading.Thread _actionThread;
        private int _lastEventLatencyMs = -1;

        // LCU 登录状态：0=未知 1=已登录 2=确定未登录
        private readonly string _lcuDir;
        private int _loginState;
        private bool _loginStateLogged;
        private DateTime _loginCheckedAt = DateTime.MinValue;
        private DateTime _lastLoginLogAt = DateTime.MinValue;
        private string _lcuLockfile;
        private volatile bool _clientRunning;
        private DateTime _clientCheckAt = DateTime.MinValue;
        private DateTime _lastWegameAllowLogAt = DateTime.MinValue;
        // 已经见过的目标窗口（只放过"第一次出现"的窗口）
        private readonly HashSet<IntPtr> _knownWindows = new HashSet<IntPtr>();

        // ---- 低级输入钩子：区分"你自己切过去的"和"进程强制抢前台" ----
        // 判定用的两个时间窗口只有一份定义（在 Rules.cs），这里只是别名，避免两边改漏一边
        private const int CLICK_GRACE_MS = FocusRules.ClickGraceMs; // 按键按下后多久内算"这次切换是你点的"
        // 任务栏/开始菜单点击的宽限：从按下到窗口真正激活可能要几百毫秒
        private const int SHELL_CLICK_GRACE_MS = 2500;
        // 「拦了没压下去」的重试间隔。原来写死 1200ms：同一个窗口再次抢前台会被
        // ProcessForeground 开头的 hwnd == _lastFgHwnd 提前 return 吞掉，只能靠这个重试兜底，
        // 所以 1.2 秒就等于「每秒抢 16 次」那种场景下大半时间不设防。
        private const int RETRY_MS = 400;
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _kbdHook = IntPtr.Zero;
        private Native.LowLevelHookProc _mouseProc;
        private Native.LowLevelHookProc _kbdProc;
        private DateTime _lastInputAt = DateTime.MinValue;
        private string _lastInputWhere = string.Empty; // 鼠标按下时指针压在哪个进程的窗口上
        private int _inputCount;
        private volatile int _inputAgeMs = int.MaxValue; // 最后一次输入距今（系统级，不信钩子）
        private string _cursorProcNow = "";
        private string _cursorProcPrev = "";
        private DateTime _lastAllowAt = DateTime.MinValue;
        private IntPtr _lastAllowHwnd = IntPtr.Zero;
        private volatile string _lastAllowProc = "";
        // 待打回的目标窗口：只有「拦过一次、它又赖在前台」才重试；用户一接管就作废
        private IntPtr _pendingBlockHwnd = IntPtr.Zero;
        private DateTime _pendingBlockAt = DateTime.MinValue;
        // 防拉锯：判据是「我们**压不动**它」，不是「我们拦了几次」。
        // 成功的拦截一次都不计入；只有动作落地后前台仍被它占着才算一次失败，
        // 10 秒内连续失败 5 次才放弃 15 秒（原来是按调用次数算，重试也算，等于一次没压住就自废一分钟）。
        private IntPtr _blockFailHwnd = IntPtr.Zero;
        private int _blockFailCount;
        private DateTime _blockFailAt = DateTime.MinValue;
        private DateTime _lastSuppressedLogAt = DateTime.MinValue;
        private readonly Dictionary<IntPtr, DateTime> _suppressedUntil = new Dictionary<IntPtr, DateTime>();
        // Alt / Win 最后一次按下的时间：这两个键才是"切窗口"用的
        private DateTime _lastModifierAt = DateTime.MinValue;
        private DateTime _lastClickAt = DateTime.MinValue;
        private string _lastClickWhere = "";
        // 按下那一刻指针命中的窗口类名：用来把"点任务栏"和"点资源管理器文件夹窗口"分开
        // （两者同属 explorer.exe，只看进程名分不出来）
        private string _lastClickClass = "";
        private bool _mouseWasDown;
        private int _clickCount;
        private const int MODIFIER_GRACE_MS = FocusRules.ModifierGraceMs;
        private const int JUST_ALLOWED_MS = 3000; // 刚放行过的窗口/程序，这段时间内不再压回去（你还在用它）

        public Guard(IEnumerable<string> targets, string logPath, bool verbose, bool noClickAllow,
            string lcuDir)
        {
            _lcuDir = lcuDir;
            _targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in targets) _targets.Add(t);
            _logPath = logPath;
            _verbose = verbose;
            _noClickAllow = noClickAllow;

            _callback = new Native.WinEventDelegate(OnForegroundChanged);
            _hook = Native.SetWinEventHook(
                Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _callback, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            // 记下启动时谁在前台，作为第一个"可回退目标"
            IntPtr fg = Native.GetForegroundWindow();
            _lastFgHwnd = fg;
            if (fg != IntPtr.Zero)
            {
                string fgProc = ProcNameOf(fg);
                if (fgProc.Length > 0 && !_targets.Contains(fgProc) &&
                    !string.Equals(fgProc, OwnProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    _lastForeground = fg;
                }
            }

            // 低级输入钩子：拿"用户到底有没有在操作"这个事实，用来区分强制抢前台和手动切换
            _mouseProc = MouseHookProc;
            _kbdProc = KeyboardHookProc;
            IntPtr hMod = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, hMod, 0);
            _kbdHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc, hMod, 0);

            _pollThread = new System.Threading.Thread(PollLoop);
            _pollThread.IsBackground = true;
            _pollThread.Priority = System.Threading.ThreadPriority.AboveNormal;
            _pollThread.Name = "LolFocusGuard-poll";
            _pollThread.Start();

            _actionThread = new System.Threading.Thread(ActionLoop);
            System.Threading.Thread clickThread = new System.Threading.Thread(ClickWatchLoop);
            clickThread.IsBackground = true;
            clickThread.Priority = System.Threading.ThreadPriority.AboveNormal;
            clickThread.Start();

            _actionThread.IsBackground = true;
            _actionThread.Priority = System.Threading.ThreadPriority.Highest;
            _actionThread.Name = "LolFocusGuard-action";
            _actionThread.Start();

            // 守卫启动前就已经存在的目标窗口不算"第一次出现"
            // 注意：这里刻意不把启动时已存在的窗口标记为"见过"——
            // 守卫如果是在登录页已经开出来之后才启动的，也应当放行一次。
            RefreshTarget(false);

            Log("守卫启动，监控进程: " + string.Join(", ", _targets));
            if (_hook == IntPtr.Zero) Log("警告：SetWinEventHook 失败，无法监听前台切换");
            if (_mouseHook == IntPtr.Zero || _kbdHook == IntPtr.Zero)
            {
                Log("警告：低级输入钩子安装失败，只能按「没输入就拦」的保守规则工作");
            }
            else
            {
                Log(string.Format("判定依据: 鼠标按键（{0}ms 内按下过就算你切的）+ 按下那一刻的指针位置；硬件状态读得到，ACE 掐不掉；低级钩子{1}",
                    CLICK_GRACE_MS, (_mouseHook != IntPtr.Zero && _kbdHook != IntPtr.Zero) ? "已安装（可能被 WeGame 的 ACE 掐掉，不影响判定）" : "没装上（不影响判定）"));
            }
        }

        public int BlockCount { get { return _blockCount; } }
        public bool Paused { get { return _paused; } }
        public DateTime ResumeAt { get { return _resumeAt; } }
        public string LogPath { get { return _logPath; } }


        /// <summary>暂停一段时间（到点自动恢复）。</summary>
        public void Pause(TimeSpan duration)
        {
            _paused = true;
            _resumeAt = DateTime.Now.Add(duration);
            Log("已暂停至 " + _resumeAt.ToString("HH:mm:ss"));
        }

        public void Resume()
        {
            _paused = false;
            _resumeAt = DateTime.MinValue;
            Log("已恢复运行");
        }

        // ---------- 轮询：记忆目标窗口状态（尤其"弹出来之前是不是最小化的"） ----------
        private void PollLoop()
        {
            while (!_disposed)
            {
                try { PollOnce(); } catch { }
                System.Threading.Thread.Sleep(POLL_MS);
            }
        }

        private void PollOnce()
        {
            try
            {
                if (_paused && _resumeAt != DateTime.MinValue && DateTime.Now >= _resumeAt)
                {
                    Resume();
                }

                // 目标窗口列表每秒刷新一次（进程重启、新窗口、pid 变化都能跟得上）
                if (_targetHwnd == IntPtr.Zero || !Native.IsWindow(_targetHwnd) ||
                    !_targets.Contains(ProcNameOf(_targetHwnd)) || (_scanTick % 20) == 0)
                {
                    RefreshTarget(false);
                }

                if (_targetHwnd != IntPtr.Zero && Native.IsWindow(_targetHwnd))
                {
                    _targetWasMinimized = Native.IsIconic(_targetHwnd);
                    if (_targetWasMinimized) _lastIconicAt = DateTime.Now;
                }

                // 每轮都更新"最后一次输入距今多久"和 Alt/Win 状态：
                // 这是不依赖任何钩子的系统级事实，安全软件（WeGame 的 ACE / SGuard）拦不住。
                UpdateInputState();

                // 指针下面是谁：每轮(50ms)采一次，并留一份上一轮的。
                // 判定"是不是你点的"时用上一轮的值 —— 理由见 ProcessForeground 里的注释。
                _cursorProcPrev = _cursorProcNow;
                _cursorProcNow = ProcUnderCursor();

                // 每 4 个 tick 扫一遍目标进程的窗口，记下谁现在是最小化的
                if ((_scanTick++ % 4) == 0) ScanIconic();
                if ((_scanTick % 20) == 0) RefreshClientRunning();
                if ((_scanTick % 40) == 0)
                {
                    try
                    {
                        RefreshLoginState(); // 约每 2 秒查一次登录状态
                    }
                    catch (Exception ex)
                    {
                        Log("登录状态检测出错: " + ex.Message);
                    }
                }

                // 兜底：钩子偶尔会漏事件（系统抖动、快速连续切换），这里再按轮询查一遍
                IntPtr fg = Native.GetForegroundWindow();
                if (fg != _lastFgHwnd)
                {
                    ProcessForeground(fg, false);
                }
                else if (fg != IntPtr.Zero && fg == _pendingBlockHwnd &&
                         (DateTime.Now - _pendingBlockAt).TotalMilliseconds > RETRY_MS)
                {
                    // 打回过一次、它却还赖在前台（上次没压下去）：再试一次。
                    // 但只要你在此期间自己放行过它（点任务栏/点窗口/Alt+Tab），这个待办立刻作废 ——
                    // 否则就是「我点了任务栏把它叫出来，1.2 秒后它又被最小化」，得点好几遍才能切出来。
                    //
                    // 还要防一种时序：你点任务栏想切回客户端，可它的「激活事件」要几百毫秒才到
                    // （慢客户端尤其明显），这时重试会以 retry=true 跑 ProcessForeground，
                    // 而 retry 分支强制忽略点击证据 → 把你正要打开的窗口又压回去。
                    // 所以判据是「这次拦截**之后**你有没有按过鼠标/Alt/Win」，而不是「最近有没有输入」；
                    // 并且按下的落点得像个切换动作（任务栏，或目标进程的窗口）。
                    bool userTakingOver =
                        (_lastClickAt > _pendingBlockAt &&
                         (DateTime.Now - _lastClickAt).TotalMilliseconds <= SHELL_CLICK_GRACE_MS &&
                         (IsTaskbarClass(_lastClickClass) || _targets.Contains(_lastClickWhere))) ||
                        (_lastModifierAt > _pendingBlockAt);

                    if (_paused || _lastAllowAt > _pendingBlockAt || userTakingOver)
                    {
                        _pendingBlockHwnd = IntPtr.Zero;
                    }
                    else
                    {
                        _lastFgHwnd = IntPtr.Zero;
                        ProcessForeground(fg, true);
                    }
                }

                if (_verbose && (_pollTicks++ % 20) == 0)
                {
                    Log(string.Format("心跳 fg={0} 按键={1} 钩子输入={2}次 钩子最后输入={3}ms前({4})",
                        ProcNameOf(fg),
                        _lastClickAt == DateTime.MinValue ? "没按过" : string.Format("{0:0}ms前@{1}", (DateTime.Now - _lastClickAt).TotalMilliseconds, _lastClickWhere.Length > 0 ? _lastClickWhere : "?"),
                        _inputCount,
                        _inputAgeMs,
                        _lastInputWhere.Length > 0 ? _lastInputWhere : "-"));
                }
            }
            catch
            {
                // 轮询永不抛出
            }
        }

        /// <summary>扫一遍所有目标进程的顶层窗口，记录当前处于最小化状态的那些。</summary>
        private void ScanIconic()
        {
            List<int> pids;
            lock (_sync)
            {
                if (_targetPids.Count == 0) return;
                pids = new List<int>(_targetPids);
            }

            DateTime now = DateTime.Now;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                bool iconic = Native.IsIconic(h);
                lock (_sync)
                {
                    if (iconic) _iconicAt[h] = now;
                    else _iconicAt.Remove(h);
                }
                return true;
            }, IntPtr.Zero);
        }

        /// <summary>这个窗口最近（seconds 秒内）是否处于最小化状态。</summary>
        private bool IsRecentlyIconic(IntPtr hwnd, double seconds)
        {
            lock (_sync)
            {
                DateTime t;
                return _iconicAt.TryGetValue(hwnd, out t) &&
                       (DateTime.Now - t).TotalSeconds < seconds;
            }
        }

        /// <summary>鼠标左/右键此刻是不是按着（读硬件状态，不需要钩子）。</summary>
        private static bool IsMouseButtonDown()
        {
            const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;
            return (Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                || (Native.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        }

        /// <summary>
        /// 专盯「鼠标真的按下去」这一刻（15ms 采一次）。
        ///
        /// 这是区分「你手动切窗口」和「它自己抢前台」最硬的证据：
        ///   手动切窗口 —— 点任务栏按钮、点客户端窗口、点通知 —— 一定伴随一次按键按下；
        ///   它自己抢前台 —— 那一下没有任何按键。
        ///
        /// 不用"最近有没有输入"当判据的原因：鼠标随手一动也算输入，太粗。
        /// 钩子会被 WeGame 的 ACE 掐掉，GetAsyncKeyState 不会 —— 它读的是硬件状态。
        /// 按下瞬间同时记下"指针落在谁身上"，就等于知道了"你点的是哪个窗口"。
        /// </summary>
        private void ClickWatchLoop()
        {
            while (true)
            {
                try
                {
                    bool down = IsMouseButtonDown();
                    if (down && !_mouseWasDown)
                    {
                        _lastClickWhere = ProcUnderCursor();
                        _lastClickClass = ClassUnderCursor();
                        _lastClickAt = DateTime.Now;
                        _clickCount++;
                    }
                    _mouseWasDown = down;
                }
                catch
                {
                }
                System.Threading.Thread.Sleep(15);
            }
        }
        /// <summary>
        /// 每 50ms 更新一次：最后一次输入距今多少毫秒、Alt/Win 是不是按着。
        /// 用 GetLastInputInfo + GetAsyncKeyState，不依赖低级钩子 ——
        /// 实测 WeGame 一起来（它带的 ACE/SGuard 安全模块）会把别人的 WH_MOUSE_LL 悄悄掐掉，
        /// 钩子方案在真正需要它的时候恰好失效，所以判定必须以系统级信息为准。
        /// </summary>
        private void UpdateInputState()
        {
            try
            {
                Native.LASTINPUTINFO lii = new Native.LASTINPUTINFO();
                lii.cbSize = (uint)Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
                if (Native.GetLastInputInfo(ref lii))
                {
                    uint age = Native.GetTickCount() - lii.dwTime;
                    _inputAgeMs = age > int.MaxValue ? int.MaxValue : (int)age;
                }
            }
            catch
            {
                _inputAgeMs = int.MaxValue;
            }

            if ((Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0 ||
                (Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 ||
                (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0)
            {
                _lastModifierAt = DateTime.Now;
            }
        }
        /// <summary>鼠标指针下面那个顶层窗口属于哪个进程。</summary>
        private static string ProcUnderCursor()        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return string.Empty;
            IntPtr under = Native.WindowFromPoint(pt);
            if (under == IntPtr.Zero) return string.Empty;
            IntPtr root = Native.GetAncestor(under, Native.GA_ROOT);
            return ProcNameOf(root == IntPtr.Zero ? under : root);
        }

        private static bool IsShellProcess(string proc) { return ProcClass.IsShell(proc); }

        /// <summary>鼠标指针下面那个窗口的类名（用来区分「任务栏」和「资源管理器文件夹窗口」）。</summary>
        private static string ClassUnderCursor()
        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return string.Empty;
            IntPtr under = Native.WindowFromPoint(pt);
            if (under == IntPtr.Zero) return string.Empty;
            // 任务栏是个容器：WindowFromPoint 命中的往往是里面的子窗口
            // （Start / TrayNotifyWnd / MSTaskSwWClass…），往上找顶层窗口再取类名。
            IntPtr h = under;
            for (int i = 0; i < 4 && h != IntPtr.Zero; i++)
            {
                string cls = ClassNameOf(h);
                if (IsTaskbarClass(cls)) return cls;
                h = Native.GetAncestor(h, Native.GA_ROOT);
            }
            return ClassNameOf(under);
        }

        /// <summary>任务栏（主屏/副屏）。只有点它才享受"激活可能很慢"的长宽限。</summary>
        private static bool IsTaskbarClass(string cls)
        {
            return string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- LCU 登录状态（只在本机 127.0.0.1 上调客户端自己的接口，不注入） ----------
        private static bool IsClientProcess(string proc) { return ProcClass.IsClient(proc); }

        private static bool IsWeGameProcess(string proc) { return ProcClass.IsWeGame(proc); }

        /// <summary>
        /// WeGame 自己的界面不是 wegame.exe 画的：实测它的页面跑在 browser.exe（class CefTopWindow、
        /// 标题 "WeGame"）和 pallas.exe（class TWINCONTROL、标题 SIDE_WEB_VIEW）里。
        /// 只看进程名会漏掉这些窗口 —— "游戏进程已启动"那张卡片就是这么漏过去的。
        /// 所以再加一条"按窗口标题/类名认"，和 wegame.exe 一视同仁。
        /// </summary>
        private static bool IsWeGameHelperWindow(string proc, string title, string cls)
        {
            // 判据本身是纯函数，放在 Rules.cs 的 ProcClass 里，LogicTest 有穷举用例
            return ProcClass.IsWeGameWindow(proc, title, cls);
        }

        /// <summary>客户端进程在不在（WeGame 登录窗口只在客户端起来之前出现，用它当判据）。</summary>
        private void RefreshClientRunning()
        {
            if ((DateTime.Now - _clientCheckAt).TotalMilliseconds < 800) return;
            _clientCheckAt = DateTime.Now;

            bool running = false;
            string[] names = { "LeagueClientUx", "LeagueClient", "RiotClientServices" };
            foreach (string n in names)
            {
                try
                {
                    Process[] ps = Process.GetProcessesByName(n);
                    if (ps.Length > 0)
                    {
                        running = true;
                        foreach (Process p in ps) p.Dispose();
                        break;
                    }
                }
                catch
                {
                }
            }
            _clientRunning = running;
        }

        private void SetLoginState(int state, string reason)
        {
            bool changed = state != _loginState || !_loginStateLogged;
            _loginState = state;
            if (changed)
            {
                _loginStateLogged = true;
                Log("LCU 登录状态: " + reason);
            }
        }

        /// <summary>每 2 秒查一次：客户端登没登录。0=未知 1=已登录 2=确定未登录。</summary>
        private void RefreshLoginState()
        {
            string lockfile = FindLockfile();
            if (lockfile == null)
            {
                SetLoginState(0, "没找到 lockfile（客户端没运行 / 目录不认识）");
                return;
            }

            string content;
            try
            {
                // 客户端是用独占方式打开 lockfile 的，File.ReadAllText（FileShare.Read）会报
                // "being used by another process"，这里必须显式允许共享读写
                using (FileStream fs = new FileStream(lockfile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs))
                {
                    content = sr.ReadToEnd().Trim();
                }
            }
            catch
            {
                SetLoginState(0, "读不到 lockfile");
                return;
            }

            // 客户端起来了但 lockfile 还是空文件 = LCU 还没就绪，人还在登录页
            if (content.Length == 0)
            {
                SetLoginState(2, "lockfile 为空（客户端还没登录）");
                return;
            }

            string[] parts = content.Split(':');
            if (parts.Length < 4)
            {
                SetLoginState(0, "lockfile 内容不认识");
                return;
            }

            int port;
            if (!int.TryParse(parts[2], out port) || port <= 0)
            {
                SetLoginState(0, "lockfile 端口不认识");
                return;
            }

            int status;
            string body;
            if (!LcuGet(port, parts[3], "/lol-summoner/v1/current-summoner", out status, out body))
            {
                SetLoginState(0, "LCU 接口连不上");
                return;
            }

            if (status == 200) SetLoginState(1, "已登录");
            else if (status == 404) SetLoginState(2, "未登录（登录页 / 安全检测阶段）");
            else SetLoginState(0, "LCU 返回 HTTP " + status);
        }

        private string FindLockfile()
        {
            if (_lcuLockfile != null && File.Exists(_lcuLockfile)) return _lcuLockfile;

            List<string> dirs = new List<string>();
            if (!string.IsNullOrEmpty(_lcuDir)) dirs.Add(_lcuDir);

            foreach (string t in _targets)
            {
                if (!IsClientProcess(t)) continue;
                try
                {
                    foreach (Process p in Process.GetProcessesByName(t))
                    {
                        try
                        {
                            string exe = p.MainModule.FileName;
                            if (!string.IsNullOrEmpty(exe)) dirs.Add(Path.GetDirectoryName(exe));
                        }
                        catch
                        {
                            // 提权进程读不到，靠下面的固定目录兜底
                        }
                        finally
                        {
                            p.Dispose();
                        }
                    }
                }
                catch
                {
                }
            }

            dirs.Add(@"C:\Riot Games\League of Legends");
            dirs.Add(@"D:\WeGameApps\英雄联盟\LeagueClient");

            foreach (string d in dirs)
            {
                if (string.IsNullOrEmpty(d)) continue;
                try
                {
                    string candidate = Path.Combine(d, "lockfile");
                    if (File.Exists(candidate))
                    {
                        _lcuLockfile = candidate;
                        return candidate;
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        private static bool LcuGet(int port, string password, string path, out int status, out string body)
        {
            status = 0;
            body = string.Empty;
            try
            {
                System.Net.ServicePointManager.ServerCertificateValidationCallback =
                    delegate { return true; };
                System.Net.HttpWebRequest req = (System.Net.HttpWebRequest)
                    System.Net.WebRequest.Create("https://127.0.0.1:" + port + path);
                req.Timeout = 1500;
                req.ReadWriteTimeout = 1500;
                req.KeepAlive = false;
                req.Headers["Authorization"] = "Basic " +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + password));
                using (System.Net.HttpWebResponse resp = (System.Net.HttpWebResponse)req.GetResponse())
                {
                    status = (int)resp.StatusCode;
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                    {
                        body = sr.ReadToEnd();
                    }
                    return true;
                }
            }
            catch (System.Net.WebException wex)
            {
                System.Net.HttpWebResponse r = wex.Response as System.Net.HttpWebResponse;
                if (r != null)
                {
                    status = (int)r.StatusCode;
                    try
                    {
                        using (StreamReader sr = new StreamReader(r.GetResponseStream()))
                        {
                            body = sr.ReadToEnd();
                        }
                    }
                    catch
                    {
                    }
                    r.Close();
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        private void RefreshTarget(bool markKnown)
        {
            List<IntPtr> wins = new List<IntPtr>();
            List<int> winPids = new List<int>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return true;
                wins.Add(h);
                winPids.Add((int)pid);
                return true;
            }, IntPtr.Zero);

            // 每个 pid 只查一次进程名，避免每轮几百次 Process 查询
            Dictionary<int, string> names = new Dictionary<int, string>();
            List<IntPtr> found = new List<IntPtr>();
            List<int> pids = new List<int>();
            for (int i = 0; i < wins.Count; i++)
            {
                string name;
                if (!names.TryGetValue(winPids[i], out name))
                {
                    name = ProcNameOf(wins[i]);
                    names[winPids[i]] = name;
                }
                if (name.Length > 0 && _targets.Contains(name))
                {
                    found.Add(wins[i]);
                    if (!pids.Contains(winPids[i])) pids.Add(winPids[i]);
                }
            }

            lock (_sync)
            {
                _targetPids.Clear();
                _targetPids.AddRange(pids);
                if (markKnown)
                {
                    foreach (IntPtr h in found) _knownWindows.Add(h);
                }
            }

            if (found.Count == 0)
            {
                _targetHwnd = IntPtr.Zero;
                return;
            }

            IntPtr best = found[0];
            long bestArea = -1;
            foreach (IntPtr h in found)
            {
                Native.RECT r;
                if (Native.GetWindowRect(h, out r))
                {
                    long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                    if (area > bestArea) { bestArea = area; best = h; }
                }
            }
            _targetHwnd = best;
        }

        // ---------- 前台切换（钩子事件 + 轮询兜底，两条路走同一套判定） ----------
        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // dwmsEventTime 是事件发生时的系统 tick，能算出"系统通知到我"的延迟
            try
            {
                int lag = Environment.TickCount - (int)dwmsEventTime;
                if (lag >= 0 && lag < 5000) _lastEventLatencyMs = lag;
            }
            catch
            {
                _lastEventLatencyMs = -1;
            }
            ProcessForeground(hwnd, false);
        }

        /// <summary>把动作丢给专用线程（比线程池快，通常 1–3ms 内执行）。</summary>
        private void QueueAction(Action action)
        {
            lock (_actionsLock)
            {
                if (_actions.Count > 8) _actions.Dequeue(); // 积压就丢最旧的
                _actions.Enqueue(action);
            }
            _actionSignal.Set();
        }

        private void ActionLoop()
        {
            while (!_disposed)
            {
                Action job = null;
                lock (_actionsLock)
                {
                    if (_actions.Count > 0) job = _actions.Dequeue();
                }
                if (job == null)
                {
                    _actionSignal.WaitOne(200);
                    continue;
                }
                try
                {
                    job();
                }
                catch (Exception ex)
                {
                    Log("动作失败: " + ex.Message);
                }
            }
        }

        /// <summary>带缓存的进程名查询（热路径专用）。</summary>
        private string ProcNameOfCached(IntPtr hwnd)        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0) return string.Empty;
            int key = (int)pid;

            lock (_pidNames)
            {
                if ((DateTime.Now - _pidNamesClearedAt).TotalSeconds > 60)
                {
                    _pidNames.Clear();
                    _pidNamesClearedAt = DateTime.Now;
                }
                string cached;
                if (_pidNames.TryGetValue(key, out cached)) return cached;
            }

            string name = ProcNameOf(hwnd);
            lock (_pidNames)
            {
                _pidNames[key] = name;
            }
            return name;
        }

        /// <param name="retry">
        /// true = "目标窗口赖在前台不动，再过一遍"的重试。重试时窗口已经在最前面了，
        /// 指针多半就压在它身上，所以绝不能把「指针在它上面」当成你点的。
        /// </param>
        private void ProcessForeground(IntPtr hwnd, bool retry)
        {
            if (hwnd == IntPtr.Zero) return;
            if (hwnd == _lastFgHwnd) return; // 这次切换已经处理过

            lock (_sync)
            {
                if (hwnd == _lastFgHwnd) return;
                _lastFgHwnd = hwnd;

                string proc = ProcNameOfCached(hwnd);
                string title = TitleOf(hwnd);
                string cls = ClassNameOf(hwnd);
                // 目标 = 静态名单里的进程，或者"客户端在跑时冒出来的 WeGame 窗口"
                // （WeGame 的界面跑在 browser/pallas 里，光按进程名认会漏）。
                bool isWeGameWin = IsWeGameProcess(proc) || IsWeGameHelperWindow(proc, title, cls);
                bool isTarget = _targets.Contains(proc) || (isWeGameWin && _clientRunning);

                if (_verbose)
                {
                    Native.RECT vr;
                    string size = Native.GetWindowRect(hwnd, out vr)
                        ? string.Format("{0}x{1}@({2},{3})", vr.Right - vr.Left, vr.Bottom - vr.Top, vr.Left, vr.Top)
                        : "?";
                    Log(string.Format("前台变化 → {0} (pid={1}) class={2} 标题=\"{3}\" 尺寸={4} 最小化={5} 按键={6}",
                        proc, ProcIdOf(hwnd), ClassNameOf(hwnd), TitleOf(hwnd), size,
                        Native.IsIconic(hwnd),
                        _lastClickAt == DateTime.MinValue
                            ? "没按过"
                            : string.Format("{0:0}ms前@{1}", (DateTime.Now - _lastClickAt).TotalMilliseconds,
                                _lastClickWhere.Length > 0 ? _lastClickWhere : "?")));
                }

                if (!isTarget)
                {
                    // 自己的窗口不算"可回退目标"
                    if (IsTopLevel(hwnd) &&
                        !string.Equals(proc, OwnProcessName, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastForeground = hwnd;
                    }
                    return;
                }

                if (_paused) return;

                // 把"事实"收集齐，交给纯函数判定 —— 规则表在 Rules.cs，可以穷举测试
                // （LogicTest.cs 不用开客户端、不用注入输入就能把每条规则跑一遍）。
                DecisionFacts facts = new DecisionFacts();
                facts.TestModeOnly = _noClickAllow;
                // 国服的登录窗口是 WeGame 弹的（不是客户端）：客户端还没起来的时候，
                // WeGame 的窗口一律放行，不然用户连登录都点不了；
                // 客户端已经起来之后再冒出来的（"游戏进程已启动"那张卡片）才拦。
                facts.WeGameLoginPhase = IsWeGameProcess(proc) && !_clientRunning;
                // 注意：这个方法有副作用（记住"这个窗口见过了"），必须调用
                facts.FirstClientWindow = IsFirstClientWindow(hwnd, proc);
                facts.Suppressed = IsSuppressed(hwnd);
                // 刚刚放行过这个窗口（或者同一个程序的窗口）→ 别再压回去
                // （"我刚点开你就给我最小化"）。3 秒内都算你还在用它。
                facts.RecentlyAllowed = (hwnd == _lastAllowHwnd ||
                                         (_lastAllowProc.Length > 0 &&
                                          string.Equals(proc, _lastAllowProc, StringComparison.OrdinalIgnoreCase))) &&
                                        (DateTime.Now - _lastAllowAt).TotalMilliseconds < JUST_ALLOWED_MS;
                facts.ModifierIdleMs = (DateTime.Now - _lastModifierAt).TotalMilliseconds;
                // 「你手动切窗口」的硬证据：鼠标物理按键真的按下了（ClickWatchLoop 每 15ms 采一次），
                // 以及按下那一刻指针落在谁身上。它自己抢前台的时候一个按键都没有，这条能把两者分开。
                facts.ClickAgeMs = (DateTime.Now - _lastClickAt).TotalMilliseconds;
                // 重试轮里窗口已经在最前面、指针必然压着它 —— 那一轮不能把"指针在它上面"算成你点的
                string where = _lastClickWhere;
                facts.ClickOnTarget = !retry && _targets.Contains(where);
                // 点任务栏：从按下到窗口真正被激活往往要几百毫秒（客户端从最小化恢复尤其慢），
                // 所以任务栏这一路单独给 2.5 秒宽限，否则「点任务栏把它叫出来」会被当场判成自弹。
                // 但只认"真的点在任务栏上"（Shell_TrayWnd / Shell_SecondaryTrayWnd）：点资源管理器
                // 的文件夹窗口、桌面图标也是 explorer 进程，那不能算"你要切到客户端"，
                // 否则客户端自弹会搭这趟顺风车漏过去。
                bool clickOnTaskbar = IsShellProcess(where) && IsTaskbarClass(_lastClickClass);
                facts.ClickOnShell = !retry && clickOnTaskbar && facts.ClickAgeMs <= SHELL_CLICK_GRACE_MS;
                if (facts.ClickOnShell) facts.ClickAgeMs = Math.Min(facts.ClickAgeMs, FocusRules.ClickGraceMs);
                facts.PrevWindowGone = _lastForeground != IntPtr.Zero && _lastForeground != hwnd &&
                                       !Native.IsWindow(_lastForeground);

                Decision decision = FocusRules.Decide(facts);
                LogDecision(decision, hwnd, where);
                if (decision.Allow)
                {
                    // 只有"有你这个人的证据"的放行才刷新保护期：点窗口、点任务栏、Alt+Tab、
                    // 窗口第一次出现、WeGame 登录阶段。
                    //
                    // 绝不能因为"刚才放行过"（AllowJustAllowed）或"正在放弃拦截期"（AllowSuppressed）
                    // 就把保护期再往后推 3 秒 —— 那样客户端只要每 2.x 秒自弹一次，就能一路续命、
                    // 永远不被拦（真机上就是"匹配成功弹窗"漏拦）。保护期必须只由**你的动作**续期。
                    bool userEvidence =
                        decision.Code == DecisionCode.AllowClickedTarget ||
                        decision.Code == DecisionCode.AllowClickedShell ||
                        decision.Code == DecisionCode.AllowModifier ||
                        decision.Code == DecisionCode.AllowFirstClientWindow ||
                        decision.Code == DecisionCode.AllowWeGameLogin;

                    if (userEvidence)
                    {
                        // 记下"刚刚放行过这个窗口" —— 这是「我刚点开你就给我最小化」的唯一防线。
                        // （之前这里漏了赋值，导致 RecentlyAllowed 永远是 false、下面的重试也永远放行不了，
                        //   真机上就是"点了任务栏把客户端叫出来，1.2 秒后又被压回去"，得点好几遍。）
                        _lastAllowAt = DateTime.Now;
                        _lastAllowHwnd = hwnd;
                        _lastAllowProc = proc;
                        // 你接管了这个窗口：任何"打回它"的待办立刻作废
                        _pendingBlockHwnd = IntPtr.Zero;
                        // 连"连续弹"的判定也一起作废：用户接管过，下一次再拦就不该按连续弹处理
                        _lastBlockedHwnd = IntPtr.Zero;
                        lock (_sync)
                        {
                            _blockFailHwnd = IntPtr.Zero;
                            _blockFailCount = 0;
                            _blockFailAt = DateTime.MinValue;
                        }
                    }
                    return;
                }

                _pendingBlockHwnd = hwnd;
                _pendingBlockAt = DateTime.Now;
                Block(hwnd);
            }
        }

        /// <summary>把判定结果写进日志（放行原因只在 verbose 下写，WeGame 登录放行在非 verbose 下也留证据）。</summary>
        private void LogDecision(Decision d, IntPtr hwnd, string where)
        {
            switch (d.Code)
            {
                case DecisionCode.AllowWeGameLogin:
                    if (_verbose) Log("放行原因: WeGame 登录阶段（客户端还没起来）");
                    else if ((DateTime.Now - _lastWegameAllowLogAt).TotalSeconds > 3)
                    {
                        // 非 verbose 下也留一条证据，方便排查"到底放行了哪个窗口"
                        _lastWegameAllowLogAt = DateTime.Now;
                        Log(string.Format("放行 WeGame 窗口（登录阶段，客户端还没起来）：class={0}，标题=\"{1}\"",
                            ClassNameOf(hwnd), TitleOf(hwnd)));
                    }
                    break;
                case DecisionCode.AllowFirstClientWindow:
                    if (_verbose) Log("放行原因: 客户端窗口第一次出现（登录页/启动页）");
                    break;
                case DecisionCode.AllowSuppressed:
                    if (_verbose) Log("放行原因: 正在放弃拦截期（刚才拦太多次）");
                    else if ((DateTime.Now - _lastSuppressedLogAt).TotalSeconds > 3)
                    {
                        // 放弃拦截期里也必须留证据：不然用户只看到"弹窗又出来了"，
                        // 会以为守卫没在跑（这条原来在非 verbose 下完全静默）。
                        _lastSuppressedLogAt = DateTime.Now;
                        Log(string.Format("放弃拦截期内放行（不再压回去）：{0}（pid={1}，标题=\"{2}\"）",
                            ProcNameOfCached(hwnd), ProcIdOf(hwnd), TitleOf(hwnd)));
                    }
                    break;
                case DecisionCode.AllowJustAllowed:
                    if (_verbose) Log("放行原因: 刚放行过这个窗口（" + JUST_ALLOWED_MS + "ms 内不重复压回去）");
                    break;
                case DecisionCode.AllowModifier:
                    if (_verbose) Log("放行原因: Alt/Win 切换");
                    break;
                case DecisionCode.AllowPrevWindowGone:
                    if (_verbose) Log("放行原因: 上一个前台窗口已经关闭，前台自动接管");
                    break;
                case DecisionCode.AllowClickedTarget:
                    if (_verbose) Log("放行原因: 你点了这个窗口（" + where + "）");
                    break;
                case DecisionCode.AllowClickedShell:
                    if (_verbose) Log("放行原因: 你点了任务栏/开始菜单（" + where + "）");
                    break;
                case DecisionCode.BlockNoClick:
                    if (_verbose)
                    {
                        Log(_lastClickAt == DateTime.MinValue
                            ? "不放行: 鼠标一次都没按过（它是自己弹的）"
                            : string.Format("不放行: {0:0}ms 内没有鼠标按键按下（它是自己弹的）",
                                (DateTime.Now - _lastClickAt).TotalMilliseconds));
                    }
                    break;
                case DecisionCode.BlockClickElsewhere:
                    if (_verbose) Log("不放行: 按键按下时指针在别的程序上（" + (where.Length > 0 ? where : "?") + "）");
                    break;
                case DecisionCode.BlockTestMode:
                    break;
            }
        }

        /// <summary>
        /// 客户端进程的窗口第一次出现（登录页 / 启动页 / 安全检测）→ 放行。
        /// 只对客户端进程有效：WeGame 的窗口另有规则（客户端没起来时全放行）。
        /// </summary>
        private bool IsFirstClientWindow(IntPtr hwnd, string proc)
        {
            if (!IsClientProcess(proc))
            {
                lock (_sync) { _knownWindows.Add(hwnd); }
                return false;
            }

            lock (_sync)
            {
                if (_knownWindows.Contains(hwnd)) return false;
                _knownWindows.Add(hwnd);
            }
            return true;
        }


        // ---------- 低级输入钩子 ----------
        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                    msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN ||
                    msg == Native.WM_MOUSEWHEEL)
                {
                    try
                    {
                        Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)
                            Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        IntPtr under = Native.WindowFromPoint(info.pt);
                        IntPtr root = Native.GetAncestor(under, Native.GA_ROOT);
                        _lastInputWhere = ProcNameOf(root == IntPtr.Zero ? under : root);
                    }
                    catch
                    {
                        _lastInputWhere = string.Empty;
                    }
                    _lastInputAt = DateTime.Now;
                    _inputCount++; // 钩子回调里不写日志（会拖慢输入），只计数，由心跳线程输出
                }
            }
            return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                try
                {
                    Native.KBDLLHOOKSTRUCT info = (Native.KBDLLHOOKSTRUCT)
                        Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                    if (msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN)
                    {
                        _lastInputAt = DateTime.Now;
                        if (info.vkCode == 0x12 || info.vkCode == 0x5B || info.vkCode == 0x5C) // Alt / Win
                        {
                            _lastModifierAt = DateTime.Now;
                        }
                    }
                }
                catch
                {
                }
            }
            return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
        }

        /// <summary>这个窗口是不是正在「放弃拦截」期里（见 Block 里的防拉锯逻辑）。</summary>
        private bool IsSuppressed(IntPtr hwnd)
        {
            lock (_sync)
            {
                DateTime until;
                if (_suppressedUntil.TryGetValue(hwnd, out until))
                {
                    if (DateTime.Now < until) return true;
                    _suppressedUntil.Remove(hwnd);
                }
            }
            return false;
        }
        /// <summary>最近这次"鼠标按下"落在哪个进程的窗口上；没按下过返回空。</summary>
        private string ClickTargetProc()
        {
            if (_noClickAllow) return string.Empty;
            if ((DateTime.Now - _lastInputAt).TotalMilliseconds < 250) return _lastInputWhere;
            return string.Empty;
        }

        /// <summary>
        /// 打回动作：先最小化（从后台弹出来的情况），再把前台交还给原来的窗口。
        /// 放到后台线程执行，避免任何一次系统调用卡住钩子线程/轮询线程。
        /// </summary>
        private void Block(IntPtr hwnd)
        {
            DateTime now = DateTime.Now;
            if ((now - _lastBlockRequestAt).TotalMilliseconds < 80) return; // 防重入抖动
            _lastBlockRequestAt = now;
            // 先取旧值再更新：escalating 要算的正是"上一次拦截到现在隔了多久"。
            // 之前是先 _lastBlockAt = now 再取，差值恒为 0，于是任何窗口被拦过第二次就永远
            // 被当成"连续弹"（而且 _lastBlockedHwnd 永不复位），一直走最强硬的最小化路径。
            DateTime previousBlockAt = _lastBlockAt;
            _lastBlockAt = now;

            int number = ++_blockCount;

            // 同一窗口在 2 秒内又来抢前台 → 说明只把焦点还回去不够（它自己还会再弹），
            // 直接最小化掉，别跟它来回拉锯。
            bool escalating = (hwnd == _lastBlockedHwnd) &&
                              (now - previousBlockAt).TotalMilliseconds < 2000;
            _lastBlockedHwnd = hwnd;

            bool wasMinimized = escalating || IsRecentlyIconic(hwnd, 5) ||
                                (_targetHwnd == hwnd && _targetWasMinimized);

            // 可回退目标必须是一个真实存在、且不属于我们自己/监控名单的窗口
            IntPtr restore = _lastForeground;
            if (restore == IntPtr.Zero || !Native.IsWindow(restore) ||
                string.Equals(ProcNameOf(restore), OwnProcessName, StringComparison.OrdinalIgnoreCase) ||
                _targets.Contains(ProcNameOf(restore)))
            {
                restore = IntPtr.Zero;
            }

            string proc = ProcNameOfCached(hwnd);
            int pid = ProcIdOf(hwnd);
            string cls = ClassNameOf(hwnd);
            string title = TitleOf(hwnd);
            Native.RECT rc;
            string size = Native.GetWindowRect(hwnd, out rc)
                ? string.Format("{0}x{1}@({2},{3})", rc.Right - rc.Left, rc.Bottom - rc.Top, rc.Left, rc.Top)
                : "?";
            int latency = _lastEventLatencyMs;

            Log(string.Format("拦截 #{0}：{1} (pid={2}) 自己弹到前台（刚才最小化={3}{4}，class={5}，标题=\"{6}\"，尺寸={7}，事件延迟={8}ms）→ 打回",
                number, proc, pid, wasMinimized ? "是" : "否",
                escalating ? "，连续弹" : "", cls, title, size,
                latency >= 0 ? latency.ToString() : "?"));

            QueueAction(delegate
            {
                try
                {
                    // 没有可以交还的窗口时（比如上一个前台窗口本身也在监控名单里），
                    // 光"不还焦点"等于什么都没做，那就直接把它最小化掉，让系统自己挑下一个窗口。
                    if (wasMinimized || restore == IntPtr.Zero)
                    {
                        Native.ShowWindow(hwnd, SW_MINIMIZE);
                        System.Threading.Thread.Sleep(5);

                        // 实测：英雄联盟的窗口（登录程序那块 TWINCONTROL）会挡掉别的进程的
                        // ShowWindow —— 调用返回 False、窗口纹丝不动，但它认
                        // WM_SYSCOMMAND/SC_MINIMIZE：PostMessage 过去立刻最小化。
                        // 所以这里必须验一下"真的最小化了吗"，没有就换条路。
                        if (!Native.IsIconic(hwnd))
                        {
                            Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MINIMIZE, IntPtr.Zero);
                            System.Threading.Thread.Sleep(15);
                            if (_verbose)
                            {
                                Log(Native.IsIconic(hwnd)
                                    ? "ShowWindow 被挡，改用 WM_SYSCOMMAND/SC_MINIMIZE 最小化成功"
                                    : "最小化失败（ShowWindow 和 WM_SYSCOMMAND 都没生效），只交还前台");
                            }
                        }
                    }
                    // 最小化后系统一般会把前台交给下一个窗口；没交就自己交还
                    IntPtr fgNow = Native.GetForegroundWindow();
                    if (_verbose && (fgNow == hwnd || restore == IntPtr.Zero))
                    {
                        Log(string.Format("打回细节: 前台还是目标={0}，交还目标={1}（{2}）",
                            fgNow == hwnd, restore == IntPtr.Zero ? "空" : ProcNameOf(restore),
                            restore == IntPtr.Zero ? "没有可交还的窗口，只最小化" : "有窗口可交还"));
                    }
                    if (fgNow == hwnd && restore != IntPtr.Zero)
                    {
                        ForceForeground(restore);
                        System.Threading.Thread.Sleep(10);
                        if (Native.GetForegroundWindow() == hwnd)
                        {
                            Native.SwitchToThisWindow(restore, false); // 最后的兜底
                            System.Threading.Thread.Sleep(10);
                        }
                        if (_verbose && Native.GetForegroundWindow() == hwnd)
                        {
                            Log("打回失败: 前台没能交还（目标窗口还占着前台）");
                        }
                    }

                    // 验收这次动作到底有没有把它从前台赶走（见 RecordBlockResult 的注释）。
                    RecordBlockResult(hwnd);
                }
                catch (Exception ex)
                {
                    Log("拦截失败: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// 拦截动作落地后的验收：前台是不是真的不在它手上了。
        ///
        /// 防拉锯的判据必须是「我们**压不动**它」，不是「我们拦了几次」。原来每调一次 Block
        /// 就计数（重试也算一次），结果是：一次没压住 + 几次 1.2s 重试 → 7 秒内凑满 6 次 →
        /// 这个窗口 60 秒免疫，等于把用户扔给弹窗一分钟。现在：
        ///   · 压下去了 → 失败计数清零（正常的"它抢一次、我们拦一次"永远累不到放弃）；
        ///   · 压不下去 → 记一次失败，10 秒内连续 5 次才放弃；
        ///   · 放弃期 15 秒（原来 60 秒），而且放弃期内每次仍然写日志（见 LogDecision）。
        /// </summary>
        private void RecordBlockResult(IntPtr hwnd)
        {
            if (Native.GetForegroundWindow() != hwnd)
            {
                lock (_sync)
                {
                    _blockFailHwnd = IntPtr.Zero;
                    _blockFailCount = 0;
                    _blockFailAt = DateTime.MinValue;
                }
                return;
            }

            bool giveUp = false;
            DateTime now = DateTime.Now;
            lock (_sync)
            {
                if (hwnd != _blockFailHwnd || (now - _blockFailAt).TotalSeconds > 10)
                {
                    _blockFailHwnd = hwnd;
                    _blockFailAt = now;
                    _blockFailCount = 0;
                }
                _blockFailCount++;
                if (_blockFailCount >= 5)
                {
                    _suppressedUntil[hwnd] = now.AddSeconds(15);
                    _blockFailCount = 0;
                    _blockFailAt = DateTime.MinValue;
                    _blockFailHwnd = IntPtr.Zero;
                    giveUp = true;
                }
            }

            if (giveUp)
            {
                Log(string.Format("放弃拦截 15 秒：{0}（pid={1}，标题=\"{2}\"）10 秒内 5 次都没能把前台从它手上拿回来，再拦就是跟它拉锯了",
                    ProcNameOfCached(hwnd), ProcIdOf(hwnd), TitleOf(hwnd)));
            }
        }

        private void ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return;

            uint fgPid;
            uint fgThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fgPid);
            uint curThread = Native.GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (fgThread != 0 && fgThread != curThread)
                {
                    attached = Native.AttachThreadInput(curThread, fgThread, true);
                }
                Native.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached) Native.AttachThreadInput(curThread, fgThread, false);
            }
        }

        // ---------- 工具 ----------
        private static bool IsTopLevel(IntPtr hwnd)
        {
            return Native.IsWindowVisible(hwnd) && Native.GetWindow(hwnd, Native.GW_OWNER) == IntPtr.Zero;
        }

        private static string ProcNameOf(IntPtr hwnd)
        {
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            return ProcNameOfPid(pid);
        }

        /// <summary>
        /// 进程名（不带 .exe）。用 QueryFullProcessImageName，比 Process.GetProcessById 快一个数量级，
        /// 轮询线程每秒要查几十次，慢一点就会把响应延迟拉到上百毫秒。
        /// </summary>
        private static string ProcNameOfPid(uint pid)
        {
            if (pid == 0) return string.Empty;

            IntPtr h = Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (Native.QueryFullProcessImageName(h, 0, sb, ref size) && size > 0)
                    {
                        string path = sb.ToString(0, size);
                        int slash = path.LastIndexOf('\\');
                        string file = slash >= 0 ? path.Substring(slash + 1) : path;
                        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            file = file.Substring(0, file.Length - 4);
                        }
                        if (file.Length > 0) return file;
                    }
                }
                catch
                {
                }
                finally
                {
                    Native.CloseHandle(h);
                }
            }

            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName;
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int ProcIdOf(IntPtr hwnd)
        {
            try
            {
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                return (int)pid;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>窗口类名（诊断用）。</summary>
        private static string ClassNameOf(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                Native.GetClassName(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>窗口标题（诊断用，截断到 40 字）。</summary>
        private static string TitleOf(IntPtr hwnd)
        {
            try
            {
                StringBuilder sb = new StringBuilder(256);
                Native.GetWindowText(hwnd, sb, sb.Capacity);
                string t = sb.ToString();
                if (t.Length > 40) t = t.Substring(0, 40) + "…";
                return t;
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Log(string message)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, message);
            try
            {
                lock (_logLock)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // 日志写不进去不影响功能
            }
        }

        public void Dispose()
        {
            if (_hook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
            if (_mouseHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
            if (_kbdHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_kbdHook);
                _kbdHook = IntPtr.Zero;
            }
            _disposed = true;
            Log("守卫退出");
        }
    }

    /// <summary>用来接收全局热键的隐藏窗口。</summary>
    internal sealed class HotkeyWindow : Form
    {
        public event Action HotkeyPressed;

        // 关键：不要因为 Show() 而抢走前台焦点
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        public HotkeyWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-4000, -4000);
            Size = new Size(1, 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && HotkeyPressed != null)
            {
                HotkeyPressed();
                return;
            }
            base.WndProc(ref m);
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Guard _guard;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _countItem;
        private readonly HotkeyWindow _hotkeyWindow;
        private readonly System.Windows.Forms.Timer _uiTimer;

        public TrayApp(Guard guard)
        {
            _guard = guard;

            _menu = new ContextMenuStrip();
            _statusItem = new ToolStripMenuItem("运行中");
            _statusItem.Enabled = false;
            _countItem = new ToolStripMenuItem("已拦截: 0 次");
            _countItem.Enabled = false;

            ToolStripMenuItem pause5 = new ToolStripMenuItem("暂停 5 分钟");
            pause5.Click += delegate { _guard.Pause(TimeSpan.FromMinutes(5)); UpdateUi(); };
            ToolStripMenuItem pause30 = new ToolStripMenuItem("暂停 30 分钟");
            pause30.Click += delegate { _guard.Pause(TimeSpan.FromMinutes(30)); UpdateUi(); };
            ToolStripMenuItem resume = new ToolStripMenuItem("恢复运行");
            resume.Click += delegate { _guard.Resume(); UpdateUi(); };
            ToolStripMenuItem openLog = new ToolStripMenuItem("打开日志");
            openLog.Click += delegate
            {
                try { Process.Start("notepad.exe", _guard.LogPath); }
                catch { }
            };
            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            exit.Click += delegate { ExitThread(); };

            _menu.Items.Add(_statusItem);
            _menu.Items.Add(_countItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(pause5);
            _menu.Items.Add(pause30);
            _menu.Items.Add(resume);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(openLog);
            _menu.Items.Add(exit);

            _icon = new NotifyIcon();
            _icon.Icon = SystemIcons.Shield;
            _icon.Text = "LOL 焦点守卫";
            _icon.ContextMenuStrip = _menu;
            _icon.Visible = true;
            _icon.DoubleClick += delegate
            {
                try { Process.Start("notepad.exe", _guard.LogPath); }
                catch { }
            };

            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.HotkeyPressed += delegate
            {
                if (_guard.Paused) _guard.Resume();
                else _guard.Pause(TimeSpan.FromMinutes(5));
                UpdateUi();
            };
            _hotkeyWindow.Show(); // 离屏 1x1 窗口，仅用于接收全局热键
            IntPtr handle = _hotkeyWindow.Handle;
            if (!Native.RegisterHotKey(handle, 1, Native.MOD_CONTROL | Native.MOD_ALT, 0x78))
            {
                _guard.Log("Ctrl+Alt+F9 热键注册失败（可能被占用），请用托盘菜单");
            }

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 1000;
            _uiTimer.Tick += delegate { UpdateUi(); };
            _uiTimer.Start();

            UpdateUi();
        }

        private void UpdateUi()
        {
            try
            {
                if (_guard.Paused)
                {
                    _statusItem.Text = string.Format("已暂停（至 {0:HH:mm:ss}）", _guard.ResumeAt);
                    _icon.Text = "LOL 焦点守卫：已暂停";
                }
                else
                {
                    _statusItem.Text = "运行中（Ctrl+Alt+F9 暂停 5 分钟）";
                    _icon.Text = "LOL 焦点守卫：运行中";
                }
                _countItem.Text = string.Format("已拦截: {0} 次", _guard.BlockCount);
            }
            catch
            {
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _uiTimer.Stop(); _uiTimer.Dispose(); } catch { }
                try { _icon.Visible = false; _icon.Dispose(); } catch { }
                try
                {
                    IntPtr handle = _hotkeyWindow.Handle;
                    Native.UnregisterHotKey(handle, 1);
                    _hotkeyWindow.Dispose();
                }
                catch { }
                try { _menu.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    internal static class Program
    {
        /// <summary>
        /// --report：把日志汇总成一份短报告（拦了多少次、都拦了谁、放行原因分布），
        /// 出问题的时候把 report.txt 发出来就够了，不用整份日志。
        /// </summary>
        private static void RunReport(string logPath)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("LolFocusGuard 日志报告");
            sb.AppendLine("日志: " + logPath);

            if (!File.Exists(logPath))
            {
                sb.AppendLine("找不到日志文件。");
                WriteReport(sb.ToString(), logPath);
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(logPath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                sb.AppendLine("读日志失败: " + ex.Message);
                WriteReport(sb.ToString(), logPath);
                return;
            }

            if (lines.Length == 0)
            {
                sb.AppendLine("日志是空的。");
                WriteReport(sb.ToString(), logPath);
                return;
            }

            string firstTime = null;
            string lastTime = null;
            int blockCount = 0;
            int giveUpCount = 0;
            int noInputCount = 0;
            int inputElsewhereCount = 0;
            Dictionary<string, int> blockedWindows = new Dictionary<string, int>();
            Dictionary<string, int> allowReasons = new Dictionary<string, int>();

            foreach (string raw in lines)
            {
                string line = raw;
                if (line.Length < 26) continue;
                if (line[0] == '[')
                {
                    string t = line.Substring(1, Math.Min(23, line.Length - 1));
                    if (firstTime == null) firstTime = t;
                    lastTime = t;
                }

                if (line.IndexOf("拦截 #", StringComparison.Ordinal) >= 0)
                {
                    blockCount++;
                    string key = DescribeWindowFromLog(line, "拦截 #");
                    if (key != null)
                    {
                        int n;
                        blockedWindows.TryGetValue(key, out n);
                        blockedWindows[key] = n + 1;
                    }
                }
                else if (line.IndexOf("放弃拦截 60 秒", StringComparison.Ordinal) >= 0)
                {
                    giveUpCount++;
                }
                else if (line.IndexOf("放行原因: ", StringComparison.Ordinal) >= 0)
                {
                    string reason = line.Substring(line.IndexOf("放行原因: ", StringComparison.Ordinal) + "放行原因: ".Length).Trim();
                    int cut = reason.IndexOf('（');
                    if (cut > 0) reason = reason.Substring(0, cut);
                    int n;
                    allowReasons.TryGetValue(reason, out n);
                    allowReasons[reason] = n + 1;
                }
                else if (line.IndexOf("不放行: ", StringComparison.Ordinal) >= 0)
                {
                    string why = line.Substring(line.IndexOf("不放行: ", StringComparison.Ordinal) + "不放行: ".Length).Trim();
                    if (why.IndexOf("没有输入", StringComparison.Ordinal) >= 0) noInputCount++;
                    else inputElsewhereCount++;
                }
            }

            sb.AppendLine(string.Format("时间范围: {0} → {1}", firstTime, lastTime));
            sb.AppendLine();
            sb.AppendLine(string.Format("拦截: {0} 次", blockCount));
            foreach (KeyValuePair<string, int> kv in blockedWindows)
            {
                sb.AppendLine(string.Format("  {0} ×{1}", kv.Key, kv.Value));
            }
            sb.AppendLine();
            sb.AppendLine("放行: " + TotalOf(allowReasons) + " 次");
            foreach (KeyValuePair<string, int> kv in allowReasons)
            {
                sb.AppendLine(string.Format("  {0} ×{1}", kv.Key, kv.Value));
            }
            sb.AppendLine();
            sb.AppendLine(string.Format("放弃拦截（防拉锯，10 秒内拦 6 次就停手）: {0} 次", giveUpCount));
            sb.AppendLine(string.Format("判为自弹的依据:「一直没有输入」{0} 次，「输入落在别的程序上」{1} 次",
                noInputCount, inputElsewhereCount));

            WriteReport(sb.ToString(), logPath);
        }

        private static int TotalOf(Dictionary<string, int> d)
        {
            int total = 0;
            foreach (KeyValuePair<string, int> kv in d) total += kv.Value;
            return total;
        }

        private static string DescribeWindowFromLog(string line, string prefix)
        {
            int i = line.IndexOf(prefix, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + prefix.Length;
            int colon = line.IndexOf('：', start);
            if (colon < 0) return null;
            int space = line.IndexOf(' ', colon + 1);
            if (space < 0) return null;
            string proc = line.Substring(colon + 1, space - colon - 1).Trim();
            int q1 = line.IndexOf("标题=\"", space, StringComparison.Ordinal);
            string title = "";
            if (q1 >= 0)
            {
                int q2 = line.IndexOf('"', q1 + 4);
                if (q2 > q1) title = line.Substring(q1 + 4, q2 - q1 - 4);
            }
            return title.Length > 0 ? proc + " / \"" + title + "\"" : proc;
        }

        private static void WriteReport(string text, string logPath)
        {
            string outPath = Path.Combine(Path.GetDirectoryName(logPath) ?? ".", "report.txt");
            try
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
            }
            catch
            {
                outPath = Path.Combine(Path.GetTempPath(), "lfg-report.txt");
                try { File.WriteAllText(outPath, text, new UTF8Encoding(false)); }
                catch { }
            }
            try
            {
                Console.WriteLine(text);
                Console.WriteLine("报告已写入: " + outPath);
            }
            catch
            {
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch { }

            // --report：把日志汇总成一份人能看懂的报告，方便排查（不需要单实例，也不改任何状态）
            if (args.Length > 0 && string.Equals(args[0], "--report", StringComparison.OrdinalIgnoreCase))
            {
                string logPath = null;
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i].StartsWith("--log=", StringComparison.OrdinalIgnoreCase))
                    {
                        logPath = args[i].Substring("--log=".Length).Trim('"');
                    }
                }
                if (logPath == null)
                {
                    logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "LolFocusGuard", "guard.log");
                }
                RunReport(logPath);
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "LolFocusGuard.SingleInstance", out created))
            {
                if (!created) return; // 已经在运行

                List<string> targets = new List<string>();
                targets.Add("LeagueClientUx");   // 英雄联盟客户端主窗口
                targets.Add("RiotClientServices"); // Riot 客户端
                targets.Add("wegame");            // WeGame 本体（启动时抢焦点那个）
                targets.Add("wegame_env");
                bool verbose = false;
                bool noClickAllow = false;
                bool targetsFromArg = false;
                string lcuDir = null;

                foreach (string arg in args)
                {
                    if (arg.StartsWith("--targets=", StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Clear();
                        string value = arg.Substring("--targets=".Length);
                        foreach (string part in value.Split(','))
                        {
                            string trimmed = part.Trim();
                            if (trimmed.Length > 0) targets.Add(trimmed);
                        }
                        targetsFromArg = true;
                    }
                    else if (string.Equals(arg, "--verbose", StringComparison.OrdinalIgnoreCase))
                    {
                        verbose = true;
                    }
                    else if (string.Equals(arg, "--no-click-allow", StringComparison.OrdinalIgnoreCase))
                    {
                        noClickAllow = true; // 仅用于自动化测试：忽略"用户点击"放行规则
                    }
                    else if (arg.StartsWith("--lcu-dir=", StringComparison.OrdinalIgnoreCase))
                    {
                        lcuDir = arg.Substring("--lcu-dir=".Length).Trim('"');
                    }
                }

                // 程序目录下有 targets.txt 就以它为准（一行一个进程名，# 开头是注释）
                if (!targetsFromArg)
                {
                    try
                    {
                        string cfg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "targets.txt");
                        if (File.Exists(cfg))
                        {
                            List<string> fromFile = new List<string>();
                            foreach (string raw in File.ReadAllLines(cfg, Encoding.UTF8))
                            {
                                string line = raw.Trim();
                                if (line.Length == 0 || line.StartsWith("#")) continue;
                                foreach (string part in line.Split(','))
                                {
                                    string trimmed = part.Trim();
                                    if (trimmed.Length > 0) fromFile.Add(trimmed);
                                }
                            }
                            if (fromFile.Count > 0) targets = fromFile;
                        }
                    }
                    catch
                    {
                        // 配置读不到就用默认值
                    }
                }

                // 程序目录下的 lcu-dir.txt：一行，指向客户端安装目录（里面应该有 lockfile）
                if (string.IsNullOrEmpty(lcuDir))
                {
                    try
                    {
                        string f = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lcu-dir.txt");
                        if (File.Exists(f))
                        {
                            foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                            {
                                string line = raw.Trim();
                                if (line.Length == 0 || line.StartsWith("#")) continue;
                                lcuDir = line;
                                break;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LolFocusGuard");
                try
                {
                    Directory.CreateDirectory(logDir);
                }
                catch
                {
                    logDir = Path.GetTempPath();
                }

                using (Guard guard = new Guard(targets, Path.Combine(logDir, "guard.log"), verbose, noClickAllow,
                    lcuDir))
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    using (TrayApp context = new TrayApp(guard))
                    {
                        Application.Run(context);
                    }
                }
            }
        }
    }
}
