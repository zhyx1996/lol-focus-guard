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

        [DllImport("user32.dll")]
        public static extern IntPtr GetParent(IntPtr hWnd);

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
        // Alt+Tab 切换器：START(0x0014) 在你按下 Alt+Tab 打开切换器时来，END(0x0015) 在你松手时来。
        // 注意别和 EVENT_SYSTEM_DIALOGSTART/END（0x0010/0x0011）搞混，那是对话框。
        public const uint EVENT_SYSTEM_SWITCHSTART = 0x0014;
        public const uint EVENT_SYSTEM_SWITCHEND = 0x0015;
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
        // 日志异步写：Log() 只入队（能在 WinEvent/钩子回调线程上安全调用，不会因为 Defender 扫盘
        // 把系统回调拖住几十毫秒）；后台线程每 200ms 或攒够 64 行批量落盘。
        private const int LOG_FLUSH_INTERVAL_MS = 200;
        private const int LOG_FLUSH_LINES = 64;
        private const int LOG_QUEUE_MAX = 4096; // 极端情况下（磁盘卡死）别把内存吃光
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue =
            new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly System.Threading.AutoResetEvent _logSignal =
            new System.Threading.AutoResetEvent(false);
        private readonly System.Threading.Thread _logThread;
        private int _logDropped;
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
        // 暂停的"显示用"截止时间（托盘/日志里给人看）；判定用的是下面的单调时钟版本
        private DateTime _resumeAt = DateTime.MinValue;
        private long _pauseStartedAtMs = Clock.Never;
        private long _pauseDurationMs;
        private int _blockCount;
        // 每个目标窗口"上一次处于最小化"的时间，用来判断弹出来的那一刻它是不是从最小化状态恢复的
        private readonly Dictionary<IntPtr, long> _iconicAt = new Dictionary<IntPtr, long>();
        private readonly List<int> _targetPids = new List<int>();
        private int _scanTick;
        private long _lastBlockRequestAt = Clock.Never;
        private int _pollTicks;
        private long _lastBlockAt = Clock.Never;
        private IntPtr _lastBlockedHwnd = IntPtr.Zero;
        // 拦截请求序号 + 该请求针对的窗口：排队的最小化动作在真正动手前要确认自己没被更新的请求取代
        // （同一个窗口连着两次请求时只执行最后一次，免得同一窗口被压两遍）。
        // 只在 Block()（回调线程）里 Interlocked 递增，动作线程只读；读到的值略旧也无非是多执行一次，不影响安全。
        private int _blockRequestSeq;
        private IntPtr _blockRequestHwnd = IntPtr.Zero;
        // 一个鼠标/Alt 证据都没有的那一轮（BlockNoClick），动作线程先等这么久再复查一次：
        // 前台事件走 WinEvent 钩子、点击证据走低级鼠标钩子或 15ms 采样线程，
        // "点击 → 窗口被激活"这条链上前台事件可能比点击证据先到。
        private const int LATE_EVIDENCE_WAIT_MS = 30;

        // pid → 进程名缓存（热路径里不能每 50ms 都去 new Process，那是几十毫秒的开销）
        private readonly Dictionary<int, string> _pidNames = new Dictionary<int, string>();
        private long _pidNamesClearedAt = Clock.NowMs();

        // 专用动作线程：比线程池调度快，延迟能压到几毫秒
        private readonly Queue<Action> _actions = new Queue<Action>();
        private readonly object _actionsLock = new object();
        private readonly System.Threading.AutoResetEvent _actionSignal = new System.Threading.AutoResetEvent(false);
        private readonly System.Threading.Thread _actionThread;
        private int _lastEventLatencyMs = -1;

        // LCU 登录状态：0=未知 1=已登录 2=确定未登录（只写日志，不参与判定，见 README）
        private readonly string _lcuDir;
        private int _loginState;
        private bool _loginStateLogged;
        private string _lcuLockfile;
        private volatile bool _clientRunning;
        private long _clientCheckAt = Clock.Never;
        private long _lastWegameAllowLogAt = Clock.Never;
        // 已经见过的目标窗口（只放过"第一次出现"的窗口）。
        // 带上限：客户端窗口增删频繁时会慢慢涨，超了就按插入顺序丢掉最旧的一批（不要求精确 LRU）。
        private const int KNOWN_WINDOWS_MAX = 512;
        private readonly HashSet<IntPtr> _knownWindows = new HashSet<IntPtr>();
        private readonly Queue<IntPtr> _knownWindowOrder = new Queue<IntPtr>();

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
        private long _lastInputAt = Clock.Never;
        private string _lastInputWhere = string.Empty; // 鼠标按下时指针压在哪个进程的窗口上
        private int _inputCount;
        private volatile int _inputAgeMs = int.MaxValue; // 最后一次输入距今（系统级，不信钩子）
        private long _lastAllowAt = Clock.Never;
        private IntPtr _lastAllowHwnd = IntPtr.Zero;
        private volatile string _lastAllowProc = "";
        // 待打回的目标窗口：只有「拦过一次、它又赖在前台」才重试；用户一接管就作废
        private IntPtr _pendingBlockHwnd = IntPtr.Zero;
        private long _pendingBlockAt = Clock.Never;
        // 防拉锯：判据是「我们**压不动**它」，不是「我们拦了几次」。
        // 成功的拦截一次都不计入；只有动作落地后前台仍被它占着才算一次失败，
        // 10 秒内连续失败 5 次才放弃 15 秒（原来是按调用次数算，重试也算，等于一次没压住就自废一分钟）。
        private IntPtr _blockFailHwnd = IntPtr.Zero;
        private int _blockFailCount;
        private long _blockFailAt = Clock.Never;
        private long _lastSuppressedLogAt = Clock.Never;
        // 放弃拦截：存"从什么时候开始放弃"（而不是"放弃到几点"），这样判定只需要算差值，
        // 不会被系统时钟往回拨搞成"无限期放弃"。
        private const int SUPPRESS_MS = 15000;
        private readonly Dictionary<IntPtr, long> _suppressedFrom = new Dictionary<IntPtr, long>();
        // Alt / Win 最后一次按下的时间：这两个键才是"切窗口"用的
        private long _lastModifierAt = Clock.Never;
        // 上面那一下按的是 Win 还是 Alt。
        // 只用来区分"明确的切窗口手势"（Win+数字/Win+方向）和"打游戏时的 Alt"
        // （LoL 里 Alt+Q/W/E/R 是自施法，按得极频繁 —— 拿它当接管理由就会偶发漏拦）。
        private volatile bool _lastModifierWasWin;
        // Alt+Tab 切换器状态（EVENT_SYSTEM_SWITCHSTART / SWITCHEND）：慢客户端兜底
        private IntPtr _switchHook = IntPtr.Zero;
        private volatile bool _altTabOpen;
        private long _lastSwitchStartAt = Clock.Never;
        private long _lastSwitchEndAt = Clock.Never;
        private int _switchCount;
        // 切换器"开着"最多认多久：SWITCHEND 丢掉时不能让守卫永久放行（见 PollOnce 里的自愈）
        private const int SWITCH_MAX_OPEN_MS = 10000;
        private long _lastClickAt = Clock.Never;
        private string _lastClickWhere = "";
        // 按下那一刻指针命中的窗口类名：用来把"点任务栏"和"点资源管理器文件夹窗口"分开
        // （两者同属 explorer.exe，只看进程名分不出来）
        private string _lastClickClass = "";
        private bool _mouseWasDown;
        private int _clickCount;      // 15ms 采样线程抓到的按下次数（钩子被 ACE 掐掉时只剩这个）
        private int _hookClickCount;  // 钩子回调抓到的按下次数（快点击不漏采）；一直不涨 = 钩子被掐了
        private const int MODIFIER_GRACE_MS = FocusRules.ModifierGraceMs;
        private const int JUST_ALLOWED_MS = 3000; // 刚放行过的**同一个窗口**，这段时间内不再压回去（你还在用它）
        // 同一个程序**别的窗口**只给 600ms：原来这一档也是 3 秒，等于"你在客户端里点过任何一处，
        // 它 3 秒内自弹什么都放行"，「匹配成功」弹窗就是从这漏过去的。
        private const int JUST_ALLOWED_PROC_MS = 600;
        // 「客户端窗口第一次出现」只在这个进程刚起来时才算（登录页/启动页/安全检测）。
        // 客户端已经跑了几分钟之后才冒出来的新窗口，不可能是登录页 —— 那就是它自己弹的。
        private const int FIRST_CLIENT_WINDOW_SECONDS = 120;
        // 每个客户端进程只放行"第一次出现"这一次（原来每个新 hwnd 都白送一次放行）。
        private readonly HashSet<int> _knownPids = new HashSet<int>();

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
            // 同一个回调再挂一个 hook 管 Alt+Tab 切换器的开/关（EVENT_SYSTEM_SWITCHSTART/END）。
            // 用一个事件范围而不是一个值：0x0014~0x0015。
            _switchHook = Native.SetWinEventHook(
                Native.EVENT_SYSTEM_SWITCHSTART, Native.EVENT_SYSTEM_SWITCHEND,
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

            // 日志落盘线程：先起来，免得下面第一句 Log 入队没人写
            _logThread = new System.Threading.Thread(LogWriterLoop);
            _logThread.IsBackground = true; // 后台线程：写盘卡住也不能拦着进程退出
            _logThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            _logThread.Name = "LolFocusGuard-log";
            _logThread.Start();

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
            if (_switchHook == IntPtr.Zero)
                Log("警告：Alt+Tab 切换器事件钩子没装上，只能靠「1.5 秒内按过 Alt」兜底（慢客户端可能被误拦）");
            if (_mouseHook == IntPtr.Zero || _kbdHook == IntPtr.Zero)
            {
                Log("警告：低级输入钩子安装失败，点击证据只能靠 15ms 采样（极快单击可能漏采），其余判定不受影响");
            }
            else
            {
                Log(string.Format("判定依据: 鼠标按键（{0}ms 内按下过就算你切的）+ 按下那一刻的指针位置；"
                    + "钩子{1}，采样线程每 15ms 兜底；Alt+Tab 切换器事件{2}",
                    CLICK_GRACE_MS,
                    _mouseHook != IntPtr.Zero ? "已安装（可能被 WeGame 的 ACE 掐掉，不影响判定）" : "没装上（不影响判定）",
                    _switchHook != IntPtr.Zero ? "已挂上" : "没挂上"));
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
            _pauseDurationMs = (long)duration.TotalMilliseconds;
            _pauseStartedAtMs = Clock.NowMs();
            _resumeAt = DateTime.Now.Add(duration); // 只用于显示（托盘/日志）
            Log("已暂停至 " + _resumeAt.ToString("HH:mm:ss"));
        }

        public void Resume()
        {
            _paused = false;
            _pauseStartedAtMs = Clock.Never;
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
                if (_paused && _pauseStartedAtMs != Clock.Never &&
                    Clock.AgeMs(_pauseStartedAtMs) >= _pauseDurationMs)
                {
                    Resume();
                }

                // Alt+Tab 切换器"开着"不能是无期限的：SWITCHEND 有可能收不到（切换器被别的程序关掉、
                // 钩子错过事件、以鼠标点选结束切换），一旦 _altTabOpen 卡在 true，就等于**永久放行一切**，
                // 守卫彻底失效。所以按"开着最多认 10 秒"自己兜底复位（正常 Alt+Tab 也就 1~2 秒）。
                if (_altTabOpen && Clock.AgeMs(_lastSwitchStartAt) > SWITCH_MAX_OPEN_MS)
                {
                    _altTabOpen = false;
                    Log("提示：Alt+Tab 切换器状态超时自动复位（没收到 SWITCHEND，最多认 " + SWITCH_MAX_OPEN_MS + "ms）");
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
                }

                // 每轮都更新"最后一次输入距今多久"和 Alt/Win 状态：
                // 这是不依赖任何钩子的系统级事实，安全软件（WeGame 的 ACE / SGuard）拦不住。
                UpdateInputState();

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
                         Clock.AgeMs(_pendingBlockAt) > RETRY_MS)
                {
                    // 打回过一次、它却还赖在前台（上次没压下去）：再试一次。
                    // 但只要你在此期间自己放行过它（点任务栏/点窗口/Alt+Tab），这个待办立刻作废 ——
                    // 否则就是「我点了任务栏把它叫出来，1.2 秒后它又被最小化」，得点好几遍才能切出来。
                    //
                    // 还要防一种时序：你点任务栏想切回客户端，可它的「激活事件」要几百毫秒才到
                    // （慢客户端尤其明显），这时重试会以 retry=true 跑 ProcessForeground，
                    // 而 retry 分支强制忽略点击证据 → 把你正要打开的窗口又压回去。
                    // 所以判据是「这次拦截**之后**你有没有按过鼠标/Alt/Win」，而不是「最近有没有输入」；
                    // 并且按下的落点得像个切换动作（shell 的点击入口，或目标进程的窗口）。
                    // "这次拦截之后你有没有按过鼠标/Modifier" 用"谁更近"来判断：
                    // AgeMs 越小越新，所以"点击比拦截更晚"= AgeMs(点击) < AgeMs(拦截)。
                    //
                    // 判据和执行前复核（ShouldStillBlock）共用 FocusRules.IsSwitchGesture：
                    // 两处如果各写一套，早晚会漂移 —— 而且这里的 Alt 也一样不能算接管
                    // （LoL 里 Alt+Q/W/E/R 是自施法，Alt 一按就取消重试，等于弹窗留在前台）。
                    double blockAge = Clock.AgeMs(_pendingBlockAt);
                    double clickAge = Clock.AgeMs(_lastClickAt);
                    double modifierAge = Clock.AgeMs(_lastModifierAt);
                    // "这次拦截之后按的、而且在宽限内"才算数（宽限和判定那一侧同一档）
                    bool clickRecent = clickAge < blockAge && clickAge <= SHELL_CLICK_GRACE_MS;
                    bool shellEntry = clickRecent && IsShellProcess(_lastClickWhere) && IsShellClickClass(_lastClickClass);
                    bool targetWindow = clickRecent && _targets.Contains(_lastClickWhere);
                    bool winPressed = modifierAge < blockAge && _lastModifierWasWin;
                    bool userTakingOver = FocusRules.IsSwitchGesture(shellEntry, targetWindow, winPressed);

                    if (_paused || Clock.AgeMs(_lastAllowAt) < blockAge || userTakingOver)
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
                    Log(string.Format("心跳 fg={0} 按键={1} 点击采样={2}/钩子={3} 切换器事件={4}次 钩子输入={5}次 最后输入={6}ms前({7})",
                        ProcNameOf(fg),
                        _lastClickAt == Clock.Never ? "没按过或已用掉" : string.Format("{0:0}ms前@{1}", Clock.AgeMs(_lastClickAt), _lastClickWhere.Length > 0 ? _lastClickWhere : "?"),
                        _clickCount,
                        _hookClickCount,
                        _switchCount,
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

            long now = Clock.NowMs();
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
                long t;
                return _iconicAt.TryGetValue(hwnd, out t) &&
                       Clock.AgeMs(t) < seconds * 1000.0;
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
                        _lastClickAt = Clock.NowMs();
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

            bool altDown = (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0;
            bool winDown = (Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 ||
                           (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0;
            if (altDown || winDown)
            {
                _lastModifierAt = Clock.NowMs();
                // Win 优先：Alt+Win 同时按着的时候按 Win 算（Win 才是明确的切窗口手势）
                _lastModifierWasWin = winDown;
            }
        }
        /// <summary>鼠标指针下面那个顶层窗口属于哪个进程。</summary>
        private static string ProcUnderCursor()
        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return string.Empty;
            return ProcUnderPoint(pt);
        }

        /// <summary>
        /// 指定屏幕坐标下面那个顶层窗口属于哪个进程。
        /// 钩子回调里用 MSLLHOOKSTRUCT.pt 调这里，采样线程用 GetCursorPos 调这里，两条路同一套算法。
        /// </summary>
        private static string ProcUnderPoint(Native.POINT pt)
        {
            IntPtr under = Native.WindowFromPoint(pt);
            if (under == IntPtr.Zero) return string.Empty;
            IntPtr root = Native.GetAncestor(under, Native.GA_ROOT);
            return ProcNameOf(root == IntPtr.Zero ? under : root);
        }

        private static bool IsShellProcess(string proc) { return ProcClass.IsShell(proc); }

        /// <summary>
        /// Windows 的"点击入口"窗口类。点这些地方都属于"你自己在切窗口/唤起程序"，
        /// 不能判成"按在别处"。分两类：
        ///   · 任务栏（Shell_TrayWnd / Shell_SecondaryTrayWnd）和任务栏缩略图预览 → 享受 2.5 秒长宽限
        ///     （从按下到窗口真正激活可能要几百毫秒，客户端从最小化恢复尤其慢）；
        ///   · 其它入口（托盘溢出面板、开始菜单、任务视图）→ 本身是顶层窗口，激活很快，
        ///     只按常规 350ms 算。
        /// 注意：资源管理器的**文件夹窗口**（CabinetWClass）不在这个名单里 ——
        /// 它同属 explorer.exe，但点它并不意味着你要切到客户端，否则客户端自弹会搭这趟顺风车。
        /// </summary>
        private static readonly string[] ShellClickClasses = new string[]
        {
            "Shell_TrayWnd",                       // 主屏任务栏
            "Shell_SecondaryTrayWnd",              // 副屏任务栏
            "TaskListThumbnailWnd",                // 任务栏缩略图预览（点它=切到那个窗口）
            "NotifyIconOverflowWindow",            // Win10 托盘溢出面板（本身就是顶层窗口）
            "TopLevelWindowForOverflowXamlIsland", // Win11 托盘溢出面板
            "Windows.UI.Core.CoreWindow",          // 开始菜单 / 通知中心（StartMenuExperienceHost · ShellExperienceHost）
            "MultitaskingViewFrame",               // 任务视图（Win10）
            "XamlExplorerHostIslandWindow"         // 任务视图 / 虚拟桌面（Win11）
        };

        /// <summary>鼠标指针下面那个窗口的类名（采样线程兜底用；算法见 ClassUnderPoint）。</summary>
        private static string ClassUnderCursor()
        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return string.Empty;
            return ClassUnderPoint(pt);
        }

        /// <summary>
        /// 指定屏幕坐标下面那个窗口的类名（用来区分「任务栏/托盘/开始菜单」和「资源管理器文件夹窗口」）。
        ///
        /// WindowFromPoint 命中的往往是容器里的子窗口（Start / TrayNotifyWnd / MSTaskSwWClass…），
        /// 所以必须沿 GetParent 逐级向上找。**不能用 GetAncestor(GA_ROOT)**：它一步就到顶层，
        /// 循环等于没转；而且托盘溢出面板、开始菜单、任务视图这些入口**本身就是顶层窗口**，
        /// 类名不在任务栏那一层，用 GA_ROOT 会把它们全当成"按在别处"→ 把用户要打开的窗口最小化。
        ///
        /// 钩子回调（MSLLHOOKSTRUCT.pt）和采样线程（GetCursorPos）都调这里，两条路语义一致。
        /// </summary>
        private static string ClassUnderPoint(Native.POINT pt)
        {
            IntPtr h = Native.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return string.Empty;

            string hit = ClassNameOf(h);
            string firstShellEntry = string.Empty;
            IntPtr cur = h;
            for (int i = 0; i < 8 && cur != IntPtr.Zero; i++)
            {
                string cls = ClassNameOf(cur);
                // 任务栏优先：缩略图预览、跳转列表这些是任务栏的子窗口/弹层，
                // 在链上能找到 Shell_TrayWnd 就该按任务栏算（长宽限）。
                if (IsTaskbarClass(cls)) return cls;
                if (firstShellEntry.Length == 0 && IsShellClickClass(cls)) firstShellEntry = cls;
                IntPtr parent = Native.GetParent(cur);
                if (parent == cur || parent == IntPtr.Zero) break;
                cur = parent;
            }
            return firstShellEntry.Length > 0 ? firstShellEntry : hit;
        }

        /// <summary>
        /// 任务栏（主屏/副屏/任务栏缩略图预览）。只有它们才享受"激活可能很慢"的长宽限。
        /// 缩略图预览（TaskListThumbnailWnd）是"点任务栏上那个小预览把最小化窗口叫回来"，
        /// 和任务栏一样是从最小化恢复，激活可能要几百毫秒 —— 按 350ms 算会误拦用户。
        /// </summary>
        private static bool IsTaskbarClass(string cls)
        {
            return string.Equals(cls, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(cls, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(cls, "TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>这是不是 shell 的"点击入口"（任务栏 / 托盘溢出 / 开始菜单 / 任务视图 / 缩略图预览）。</summary>
        private static bool IsShellClickClass(string cls)
        {
            if (cls == null || cls.Length == 0) return false;
            for (int i = 0; i < ShellClickClasses.Length; i++)
            {
                if (string.Equals(cls, ShellClickClasses[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
            if (Clock.AgeMs(_clientCheckAt) < 800) return;
            _clientCheckAt = Clock.NowMs();

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
            // 客户端（含启动器）全都退出了 → "每个 pid 只放行一次"的记录清零，
            // 否则下次客户端重启后万一被系统复用同一个 pid，登录页就再也放行不了了。
            if (!running)
            {
                lock (_sync)
                {
                    if (_knownPids.Count > 0) _knownPids.Clear();
                }
            }
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
                    foreach (IntPtr h in found) RememberWindow(h);
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
            // Alt+Tab 切换器的开/关事件也走这个回调（挂了第二个 hook）。
            // 这里只记两个字段就返回，不做任何窗口/进程查询 —— 回调跑在我们的消息循环线程上，
            // 拖慢它会连带拖慢后面的前台事件。
            if (eventType == Native.EVENT_SYSTEM_SWITCHSTART)
            {
                _altTabOpen = true;
                _lastSwitchStartAt = Clock.NowMs();
                _switchCount++;
                return;
            }
            if (eventType == Native.EVENT_SYSTEM_SWITCHEND)
            {
                _altTabOpen = false;
                _lastSwitchEndAt = Clock.NowMs();
                _switchCount++;
                return;
            }

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
                if (Clock.AgeMs(_pidNamesClearedAt) > 60000)
                {
                    _pidNames.Clear();
                    _pidNamesClearedAt = Clock.NowMs();
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

        /// <summary>
        /// 这一次前台变化如果是「那一下点击」引起的，就把点击证据消费掉（用一次就没了）。
        ///
        /// 为什么"非目标窗口"那条路也必须调用：你点任务栏切到浏览器/别的程序时，
        /// ProcessForeground 在 isTarget=false 那里就 return 了；如果那儿不消费，2.5 秒内
        /// 客户端自弹仍然会被判成"你点的" —— 这正是 P1-8 要砍的顺风车。
        ///
        /// 调用方必须已持有 _sync（和 ProcessForeground 同一个临界区）。
        /// </summary>
        private void ConsumeClickEvidenceIfUsed()
        {
            long usedClickAt = _lastClickAt;
            if (usedClickAt == Clock.Never) return;

            double age = Clock.AgeMs(usedClickAt);
            string where = _lastClickWhere;
            bool shellClick = IsShellProcess(where) && IsShellClickClass(_lastClickClass) &&
                              age <= SHELL_CLICK_GRACE_MS;
            bool clickOnTarget = _targets.Contains(where) && age <= CLICK_GRACE_MS;
            if (!shellClick && !clickOnTarget) return;

            // 只消费"刚刚判定用的那一份"：用户在这中间新按的一下不能被吃掉。
            if (_lastClickAt != usedClickAt) return;

            _lastClickAt = Clock.Never; // 证据用掉
            _lastClickWhere = string.Empty;
            _lastClickClass = string.Empty;
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
                        _lastClickAt == Clock.Never
                            ? "没按过或已用掉"
                            : string.Format("{0:0}ms前@{1}", Clock.AgeMs(_lastClickAt),
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
                    // 这一次前台变化如果是那一下点击引起的（比如点任务栏切到浏览器），
                    // 这份点击证据同样算"用掉了"。否则 2.5 秒内客户端自弹会搭这趟顺风车：
                    // 你点了任务栏去切浏览器，2 秒后客户端自己弹前台，还被判成"你点的"。
                    ConsumeClickEvidenceIfUsed();
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
                // 这个 hwnd 在本次判定之前是不是已经见过 —— 必须在 IsFirstClientWindow 之前取，
                // 因为它会把当前 hwnd 写进 _knownWindows（副作用）。
                bool knownBefore;
                lock (_sync) { knownBefore = _knownWindows.Contains(hwnd); }
                // 注意：这个方法有副作用（记住"这个窗口见过了"），必须调用
                facts.FirstClientWindow = IsFirstClientWindow(hwnd, proc);
                facts.Suppressed = IsSuppressed(hwnd);
                // 刚刚放行过 → 别再压回去（"我刚点开你就给我最小化"）。按两种情况分开给：
                //   · 同一个 hwnd：3 秒（你刚才就在用它，这条是"点不出来"的防线，不能动）；
                //   · 同进程的**另一个已知窗口**：600ms（再宽就成了"点过客户端任何一处，
                //    它 3 秒内自弹什么都被放行"；客户端新造的 hwnd 连这条都不给）。
                double allowAgeMs = Clock.AgeMs(_lastAllowAt);
                bool sameHwnd = hwnd == _lastAllowHwnd;
                bool sameProc = !sameHwnd && knownBefore && _lastAllowProc.Length > 0 &&
                                string.Equals(proc, _lastAllowProc, StringComparison.OrdinalIgnoreCase);
                facts.SameWindowJustAllowed = sameHwnd && allowAgeMs < JUST_ALLOWED_MS;
                facts.SameProcJustAllowed = sameProc && allowAgeMs < JUST_ALLOWED_PROC_MS;
                facts.ModifierIdleMs = Clock.AgeMs(_lastModifierAt);
                // Alt+Tab 兜底：切换器开着，或者刚松开（SWITCHEND 之后 2.5 秒内）。
                // 慢客户端"Alt+Tab 过去 2 秒后才画出来"就靠这条救，1.5 秒的 Alt 宽限不够。
                facts.Switching = _altTabOpen;
                facts.SwitchEndedMs = Clock.AgeMs(_lastSwitchEndAt);
                // 「你手动切窗口」的硬证据：鼠标物理按键真的按下了（ClickWatchLoop 每 15ms 采一次），
                // 以及按下那一刻指针落在谁身上。它自己抢前台的时候一个按键都没有，这条能把两者分开。
                // 记下本次判定用的那一份点击证据（下面"用完即弃"要确认消费的还是同一份，
                // 不能把用户在这中间新按的一下吃掉）。
                long usedClickAt = _lastClickAt;
                facts.ClickAgeMs = Clock.AgeMs(usedClickAt);
                // 重试轮里窗口已经在最前面、指针必然压着它 —— 那一轮不能把"指针在它上面"算成你点的
                string where = _lastClickWhere;
                facts.ClickOnTarget = !retry && _targets.Contains(where);
                // 点任务栏 / 托盘溢出 / 开始菜单 / 任务视图 / 缩略图预览：
                //   · 任务栏本身给 2.5 秒宽限（从按下到窗口真正被激活往往要几百毫秒，
                //     客户端从最小化恢复尤其慢），否则「点任务栏把它叫出来」会被当场判成自弹；
                //   · 其它入口（本身就是顶层窗口，激活很快）只按常规 350ms 算。
                // 两者都要求指针下面是 shell 进程，且类名在 ShellClickClasses 白名单里 ——
                // 资源管理器的文件夹窗口、桌面图标同属 explorer，但不是"点击入口"，
                // 那不算"你要切到客户端"，否则客户端自弹会搭这趟顺风车漏过去。
                bool shellEntry = IsShellProcess(where) && IsShellClickClass(_lastClickClass);
                bool clickOnTaskbar = shellEntry && IsTaskbarClass(_lastClickClass);
                double shellGraceMs = clickOnTaskbar ? SHELL_CLICK_GRACE_MS : FocusRules.ClickGraceMs;
                facts.ClickOnShell = !retry && shellEntry && facts.ClickAgeMs <= shellGraceMs;
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
                        decision.Code == DecisionCode.AllowSwitching ||
                        decision.Code == DecisionCode.AllowFirstClientWindow ||
                        decision.Code == DecisionCode.AllowWeGameLogin;

                    if (userEvidence)
                    {
                        // 记下"刚刚放行过这个窗口" —— 这是「我刚点开你就给我最小化」的唯一防线。
                        // （之前这里漏了赋值，导致"刚放行过"永远是 false、下面的重试也永远放行不了，
                        //   真机上就是"点了任务栏把客户端叫出来，1.2 秒后又被压回去"，得点好几遍。）
                        // 注意：AllowJustAllowed / AllowJustAllowedProc 不在 userEvidence 里 ——
                        // 那两种放行本身是"上次放行的余温"，不能用来续期，否则客户端每 2.x 秒
                        // 自弹一次就能无限续命（真机已复现）。
                        _lastAllowAt = Clock.NowMs();
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
                            _blockFailAt = Clock.Never;
                        }
                    }

                    // 点击证据「用完即弃」：这次前台变化就是那一次点击引起的，同一份证据不能再
                    // 放行后面任何一次自弹。否则"点任务栏切到浏览器 → 2 秒后客户端自弹"会搭这趟
                    // 顺风车（2.5 秒的宽限本来是给"激活慢"的，不该变成"2.5 秒内随便弹都放行"）。
                    // 误拦的风险由上面刷新过的"同一个 hwnd 3 秒保护"兜底（那条不受影响）。
                    if (decision.Code == DecisionCode.AllowClickedTarget ||
                        decision.Code == DecisionCode.AllowClickedShell)
                    {
                        ConsumeClickEvidenceIfUsed();
                    }
                    return;
                }

                _pendingBlockHwnd = hwnd;
                _pendingBlockAt = Clock.NowMs();
                // 只有"什么证据都没有"的那一轮才值得等 30ms 复查（有证据的轮次多等也是白等）
                Block(hwnd, decision.Code == DecisionCode.BlockNoClick);
            }
        }

        /// <summary>把判定结果写进日志（放行原因只在 verbose 下写，WeGame 登录放行在非 verbose 下也留证据）。</summary>
        private void LogDecision(Decision d, IntPtr hwnd, string where)
        {
            switch (d.Code)
            {
                case DecisionCode.AllowWeGameLogin:
                    if (_verbose) Log("放行原因: WeGame 登录阶段（客户端还没起来）");
                    else if (Clock.AgeMs(_lastWegameAllowLogAt) > 3000)
                    {
                        // 非 verbose 下也留一条证据，方便排查"到底放行了哪个窗口"
                        _lastWegameAllowLogAt = Clock.NowMs();
                        Log(string.Format("放行 WeGame 窗口（登录阶段，客户端还没起来）：class={0}，标题=\"{1}\"",
                            ClassNameOf(hwnd), TitleOf(hwnd)));
                    }
                    break;
                case DecisionCode.AllowFirstClientWindow:
                    if (_verbose) Log("放行原因: 客户端窗口第一次出现（登录页/启动页）");
                    break;
                case DecisionCode.AllowSuppressed:
                    if (_verbose) Log("放行原因: 正在放弃拦截期（刚才拦太多次）");
                    else if (Clock.AgeMs(_lastSuppressedLogAt) > 3000)
                    {
                        // 放弃拦截期里也必须留证据：不然用户只看到"弹窗又出来了"，
                        // 会以为守卫没在跑（这条原来在非 verbose 下完全静默）。
                        _lastSuppressedLogAt = Clock.NowMs();
                        Log(string.Format("放弃拦截期内放行（不再压回去）：{0}（pid={1}，标题=\"{2}\"）",
                            ProcNameOfCached(hwnd), ProcIdOf(hwnd), TitleOf(hwnd)));
                    }
                    break;
                case DecisionCode.AllowJustAllowed:
                    if (_verbose) Log("放行原因: 刚放行过这个窗口（同一个窗口 " + JUST_ALLOWED_MS + "ms 内不重复压回去）");
                    break;
                case DecisionCode.AllowJustAllowedProc:
                    if (_verbose) Log("放行原因: 同一个程序刚放行过别的窗口（" + JUST_ALLOWED_PROC_MS + "ms 内）");
                    break;
                case DecisionCode.AllowModifier:
                    if (_verbose) Log("放行原因: Alt/Win 切换");
                    break;
                case DecisionCode.AllowSwitching:
                    if (_verbose)
                    {
                        Log(_altTabOpen
                            ? "放行原因: Alt+Tab 切换器还开着（还没松手，是你在切）"
                            : string.Format("放行原因: 刚用 Alt+Tab 切完窗口（切换器关掉 {0:0}ms，{1}ms 内都算你切的）",
                                Clock.AgeMs(_lastSwitchEndAt), FocusRules.SwitchEndGraceMs));
                    }
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
                        Log(_lastClickAt == Clock.Never
                            ? "不放行: 没有尚未用掉的鼠标按键（它是自己弹的）"
                            : string.Format("不放行: {0:0}ms 内没有鼠标按键按下（它是自己弹的）",
                                Clock.AgeMs(_lastClickAt)));
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
        ///
        /// 两个收紧（原来"每个新 hwnd 都放行一次"，客户端每弹一个新窗口就白送一次放行）：
        ///   1) 每个 pid 只放行一次（一个客户端实例的登录/启动页只有一次）；
        ///   2) 且这个进程是"刚起来"的（< 120 秒）——已经跑了几分钟的进程再冒出新窗口，
        ///      不可能是登录页，那就是它自己弹的。
        ///
        /// 取进程启动时间可能因为权限/反作弊拿不到：这时退化为"只按 pid 一次"，
        /// 宁可少拦一次，也不能把登录页拦了（登录页必须放行是硬要求）。
        ///
        /// 刻意**不**用 LCU 登录状态当判据：真机实测腾讯版客户端的 lockfile 恒为空文件，
        /// _loginState 会永远停在"确定未登录(2)"，拿它当放行条件等于把这条收紧完全废掉
        /// （见 README「为什么不用登录状态判断」）。
        /// </summary>
        private bool IsFirstClientWindow(IntPtr hwnd, string proc)
        {
            if (!IsClientProcess(proc))
            {
                lock (_sync) { RememberWindow(hwnd); }
                return false;
            }

            uint pidU;
            Native.GetWindowThreadProcessId(hwnd, out pidU);
            int pid = (int)pidU;

            lock (_sync)
            {
                if (_knownWindows.Contains(hwnd)) return false;
                RememberWindow(hwnd);
                // 这个进程已经放行过一次了 → 之后它的任何新窗口都不算"第一次出现"
                if (pid != 0 && !_knownPids.Add(pid)) return false;
            }

            int ageSec = ClientProcessAgeSeconds(pid);
            if (ageSec < 0) return true;      // 拿不到启动时间：退化为"只按 pid 一次"
            return ageSec <= FIRST_CLIENT_WINDOW_SECONDS;
        }

        /// <summary>
        /// 记住"这个窗口见过了"，并维持上限：客户端窗口增删频繁时 _knownWindows 会慢慢涨，
        /// 超了就按插入顺序丢掉最旧的一批（不要求精确 LRU —— 丢早了只是可能把老窗口
        /// 重新当成"第一次出现"，而"每 pid 只放行一次"已经把这条兜住了）。
        /// **调用方必须持有 _sync。**
        /// </summary>
        private void RememberWindow(IntPtr hwnd)
        {
            if (!_knownWindows.Add(hwnd)) return;
            _knownWindowOrder.Enqueue(hwnd);
            if (_knownWindowOrder.Count > KNOWN_WINDOWS_MAX)
            {
                int drop = KNOWN_WINDOWS_MAX / 4;
                for (int i = 0; i < drop; i++)
                {
                    IntPtr old = _knownWindowOrder.Dequeue();
                    _knownWindows.Remove(old);
                }
            }
        }

        /// <summary>客户端进程已经跑了多少秒；拿不到（没权限/进程已退出）返回 -1。</summary>
        private static int ClientProcessAgeSeconds(int pid)
        {
            if (pid <= 0) return -1;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                {
                    // 这里用 DateTime 是对的：进程启动时间是**墙上时钟**的绝对时刻，
                    // 和单调时钟没有可比性；而且只用来判断"是不是刚起来"，误差几秒无所谓。
                    double sec = (DateTime.Now - p.StartTime).TotalSeconds;
                    if (sec < 0 || sec > int.MaxValue) return -1;
                    return (int)sec;
                }
            }
            catch
            {
                return -1;
            }
        }


        // ---------- 低级输入钩子 ----------
        private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isButtonDown = msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN ||
                                    msg == Native.WM_MBUTTONDOWN || msg == Native.WM_XBUTTONDOWN;
                if (isButtonDown || msg == Native.WM_MOUSEWHEEL)
                {
                    // 回调跑在输入线程上：这里只做 WindowFromPoint + 类名/进程名查询，
                    // 不写日志、不加锁、不分配大对象、不碰事件队列 —— 拖慢它等于拖慢整机输入。
                    try
                    {
                        Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)
                            Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        // 注意：MSLLHOOKSTRUCT.pt 是**屏幕像素坐标**，和我们进程（已 SetProcessDPIAware）
                        // 里 GetCursorPos 拿到的坐标系一致，所以可以直接喂给 WindowFromPoint。
                        string proc = ProcUnderPoint(info.pt);
                        _lastInputWhere = proc;
                        if (isButtonDown && msg != Native.WM_XBUTTONDOWN)
                        {
                            // 左/右/中键都算"点了一下"。侧键（XBUTTON）不切窗口，只记输入；
                            // 而且它和左键常常成对按下，记进来会让"你点了它"变成误判。
                            // 和 15ms 采样线程写同一组字段、同一个顺序（时间戳最后写，
                            // 当作"这一份证据生效"的提交点）。两路并行最多是重复采到同一次按下，
                            // 谁后写谁覆盖，字段之间不会错配。
                            string cls = ClassUnderPoint(info.pt);
                            _lastClickWhere = proc;
                            _lastClickClass = cls;
                            _lastClickAt = Clock.NowMs();
                            _hookClickCount++;
                        }
                    }
                    catch
                    {
                        _lastInputWhere = string.Empty;
                    }
                    _lastInputAt = Clock.NowMs();
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
                        _lastInputAt = Clock.NowMs();
                        if (info.vkCode == 0x12 || info.vkCode == 0x5B || info.vkCode == 0x5C) // Alt / Win
                        {
                            _lastModifierAt = Clock.NowMs();
                            _lastModifierWasWin = (info.vkCode == 0x5B || info.vkCode == 0x5C);
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
                long from;
                if (_suppressedFrom.TryGetValue(hwnd, out from))
                {
                    // 存的是"从什么时候开始放弃"，不是"放弃到几点"：
                    // 判定只算差值，系统时钟往回拨也不会变成无限期放弃。
                    if (Clock.AgeMs(from) < SUPPRESS_MS) return true;
                    _suppressedFrom.Remove(hwnd);
                }
            }
            return false;
        }
        /// <summary>最近这次"鼠标按下"落在哪个进程的窗口上；没按下过返回空。</summary>
        private string ClickTargetProc()
        {
            if (_noClickAllow) return string.Empty;
            if (Clock.AgeMs(_lastInputAt) < 250) return _lastInputWhere;
            return string.Empty;
        }

        /// <summary>
        /// 打回动作：先最小化（从后台弹出来的情况），再把前台交还给原来的窗口。
        /// 放到后台线程执行，避免任何一次系统调用卡住钩子线程/轮询线程。
        /// </summary>
        /// <param name="waitForLateEvidence">
        /// true = 本轮判定码是 BlockNoClick（一个鼠标/Alt 证据都没有）。这种轮次的"成因点击"
        /// 可能只是还没被钩子/15ms 采样线程记下来，所以动作线程先等 LATE_EVIDENCE_WAIT_MS 再复查。
        /// </param>
        private void Block(IntPtr hwnd, bool waitForLateEvidence)
        {
            long now = Clock.NowMs();
            if (Clock.AgeMs(_lastBlockRequestAt) < 80) return; // 防重入抖动
            _lastBlockRequestAt = now;
            // 先取旧值：escalating 要算的正是"上一次拦截到现在隔了多久"。
            // 之前是先 _lastBlockAt = now 再取，差值恒为 0，于是任何窗口被拦过第二次就永远
            // 被当成"连续弹"（而且 _lastBlockedHwnd 永不复位），一直走最强硬的最小化路径。
            long previousBlockAt = _lastBlockAt;

            // 同一窗口在 2 秒内又来抢前台 → 说明只把焦点还回去不够（它自己还会再弹），
            // 直接最小化掉，别跟它来回拉锯。
            // 注意：这里只做"快照判断"，_lastBlockAt / _lastBlockedHwnd 的更新挪到动作真的执行时
            // （否则一次被取消的拦截也会算成"刚拦过"，让下一次无端升级成最小化）。
            bool escalating = (hwnd == _lastBlockedHwnd) &&
                              Clock.AgeMs(previousBlockAt) < 2000;

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

            // 日志用的快照。注意 "拦截 #N" 这行日志挪到了动作线程里、复核通过之后才写：
            // 否则"先记了拦截、又取消了"会让日志里的拦截次数、托盘计数和测试脚本的统计全都偏大
            // （test-user-intent 断言"保护期内 0 次拦截"、test-fake-client 数拦截次数，都吃这个）。
            string proc = ProcNameOfCached(hwnd);
            int pid = ProcIdOf(hwnd);
            string cls = ClassNameOf(hwnd);
            string title = TitleOf(hwnd);
            Native.RECT rc;
            string size = Native.GetWindowRect(hwnd, out rc)
                ? string.Format("{0}x{1}@({2},{3})", rc.Right - rc.Left, rc.Bottom - rc.Top, rc.Left, rc.Top)
                : "?";
            int latency = _lastEventLatencyMs;

            // 这个请求的"身份证"：动作排队之后可能被更新的请求取代，也可能你已经接管了
            int requestId = System.Threading.Interlocked.Increment(ref _blockRequestSeq);
            _blockRequestHwnd = hwnd;
            long requestAt = now;

            QueueAction(delegate
            {
                try
                {
                    // ---------- P0-B：没有点击证据的那一轮，先等 30ms 再看一眼 ----------
                    // 只在动作线程里等：Block() 本身跑在前台事件回调线程上，在那里 sleep 会拖慢
                    // 后面所有前台事件。有证据的轮次完全不进这里（多等 30ms 也是白等）。
                    if (waitForLateEvidence)
                    {
                        System.Threading.Thread.Sleep(LATE_EVIDENCE_WAIT_MS);
                        double lateClickAge = Clock.AgeMs(_lastClickAt);
                        double lateModifierAge = Clock.AgeMs(_lastModifierAt);
                        // BlockNoClick 的前提就是"判定那一刻点击已经 > 350ms、Alt/Win 已经 > 1500ms"，
                        // 所以这两个值在等完之后仍落在宽限内，只可能是这 30ms 里新到的证据。
                        // Modifier 这条同样只认 Win：Alt 自施法按得太频繁，不能拿它放弃拦截
                        // （真按了 Alt+Tab 的话，下面 ShouldStillBlock 的"还是不是前台"会把它挡掉）。
                        bool lateWin = _lastModifierWasWin && lateModifierAge <= FocusRules.ModifierGraceMs;
                        if (lateClickAge <= FocusRules.ClickGraceMs || lateWin)
                        {
                            CancelBlocked(requestId, hwnd,
                                string.Format("点击证据晚到（等 {0}ms 后按键={1}，Win={2}）→ 放弃这次拦截",
                                    LATE_EVIDENCE_WAIT_MS, DescribeAge(lateClickAge),
                                    _lastModifierWasWin ? DescribeAge(lateModifierAge) : "没按"));
                            return;
                        }
                    }

                    // ---------- P0-A：排队这段时间世界可能已经变了，动手前再复核一次 ----------
                    string why;
                    if (!ShouldStillBlock(hwnd, requestId, requestAt, out why))
                    {
                        CancelBlocked(requestId, hwnd, why);
                        return;
                    }

                    int number = ++_blockCount;
                    _lastBlockAt = Clock.NowMs();
                    _lastBlockedHwnd = hwnd;

                    Log(string.Format("拦截 #{0}：{1} (pid={2}) 自己弹到前台（刚才最小化={3}{4}，class={5}，标题=\"{6}\"，尺寸={7}，事件延迟={8}ms）→ 打回",
                        number, proc, pid, wasMinimized ? "是" : "否",
                        escalating ? "，连续弹" : "", cls, title, size,
                        latency >= 0 ? latency.ToString() : "?"));

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
        /// 排队的最小化动作"临动手前"的复核：还该不该拦。
        ///
        /// 为什么必须有这一步：Block() 在前台事件回调线程上跑，真正的最小化却排在专用动作线程上；
        /// 动作线程可能正被上一个 job 占着（每个 job 里有 5/15/10ms 的 sleep），排队几十毫秒很正常。
        /// 这几十毫秒里你按了任务栏 / 按了 Alt+Tab 打算自己接管，_pendingBlockHwnd 只会取消
        /// "后续重试"，**已经排队的这一个动作照样会执行** —— 真机表现就是"我明明点出来了，
        /// 又被压回去一下，得点好几遍"。所以每条判据都在执行前重新取一次，不再用入队时的快照。
        /// </summary>
        private bool ShouldStillBlock(IntPtr hwnd, int requestId, long requestAt, out string why)
        {
            why = null;
            if (_disposed) { why = "守卫正在退出"; return false; }
            if (_paused) { why = "你已经暂停了守卫"; return false; }
            if (!Native.IsWindow(hwnd)) { why = "窗口已经不存在了"; return false; }

            // 它已经不是前台了 → 没必要再压（很可能已经被别的路径/系统自己换掉了）
            IntPtr fgNow = Native.GetForegroundWindow();
            if (fgNow != hwnd)
            {
                why = string.Format("它已经不是前台窗口了（现在是 {0}）",
                    fgNow == IntPtr.Zero ? "无" : ProcNameOfCached(fgNow));
                return false;
            }

            // "入队之后你做了个**像切窗口**的动作" = 这中间你已经接管了。
            // AgeMs 越小越新，所以"比这次请求更晚" = AgeMs(证据) < AgeMs(请求时刻)。
            //
            // 判据必须认落点、只认 Win（FocusRules.IsSwitchGesture，规则层有穷举用例）：
            //   · 游戏里右键走位（每秒好几次）、点桌面、点聊天框，都是"按下了鼠标"，
            //     但不是"你要切窗口" —— 拿它们取消拦截，真机表现就是「偶发漏拦」；
            //   · LoL 里 Alt+Q/W/E/R 是自施法，Alt 按得极频繁，同理不能当接管证据。
            double reqAge = Clock.AgeMs(requestAt);
            double clickAge = Clock.AgeMs(_lastClickAt);
            bool clickNewer = clickAge < reqAge;
            bool shellEntry = clickNewer && IsShellProcess(_lastClickWhere) && IsShellClickClass(_lastClickClass);
            bool targetWindow = clickNewer && _targets.Contains(_lastClickWhere);
            double modifierAge = Clock.AgeMs(_lastModifierAt);
            bool winPressed = modifierAge < reqAge && _lastModifierWasWin;

            if (FocusRules.IsSwitchGesture(shellEntry, targetWindow, winPressed))
            {
                if (winPressed)
                    why = string.Format("排队之后你按了 Win（{0}ms前）", modifierAge);
                else if (shellEntry)
                    why = string.Format("排队之后你点了任务栏/开始菜单类入口（{0:0}ms前，class={1}）",
                        clickAge, _lastClickClass.Length > 0 ? _lastClickClass : "?");
                else
                    why = string.Format("排队之后你点了被监控的窗口（{0:0}ms前@{1}）",
                        clickAge, _lastClickWhere.Length > 0 ? _lastClickWhere : "?");
                return false;
            }

            // 同一个窗口已经有更新的请求排在后面了：让它去执行（它拿的是更新的 restore/escalating
            // 快照），这一个作废，免得同一个窗口被连着压两次。
            // 只按"同一个窗口"取代：两个不同窗口先后弹（A 然后 B）时，A 的那一次照样该压回去，
            // 否则 A 就永远留在了前台。
            if (requestId != _blockRequestSeq && hwnd == _blockRequestHwnd)
            {
                why = "已经被更新的拦截请求取代";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 把"距今多少毫秒"写成日志友好的样子：从没发生过（AgeMs 返回 double.MaxValue）
        /// 时不能直接 {0:0} —— 会打出 1797693134862320000… 这种一串天文数字。
        /// </summary>
        private static string DescribeAge(double ageMs)
        {
            if (ageMs >= double.MaxValue / 2) return "从没发生过";
            return string.Format("{0:0}ms前", ageMs);
        }

        /// <summary>
        /// 排队的最小化动作被取消。只在详细日志里留痕（普通日志只该出现真的拦下来的那些，
        /// 否则用户/脚本数"拦截次数"会偏大），并连带取消它的"待重试"。
        /// </summary>
        private void CancelBlocked(int requestId, IntPtr hwnd, string why)
        {
            if (_verbose)
            {
                // 注意用词：这里不能写成 "拦截 #N：进程名" 的形式 —— 测试脚本是按
                // /拦截 #\d+：进程名/ 数拦截次数的，取消不算拦下来的一次。
                Log(string.Format("取消拦截（请求 #{0}）：{1} (pid={2}) —— {3}，这次不压回去",
                    requestId, ProcNameOfCached(hwnd), ProcIdOf(hwnd), why));
            }
            // 连"待重试"一起取消：否则 400ms 后同一条待办还会再拦一次，你按的那一下等于白按
            // （这正是"点了任务栏把它叫出来，1.2 秒后又被压回去"的另一个来源）。
            // 只有最新那个请求有权清：更新的请求已经把待办换成它自己的了，别替它清掉。
            if (requestId == _blockRequestSeq && _pendingBlockHwnd == hwnd)
            {
                _pendingBlockHwnd = IntPtr.Zero;
            }
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
                    _blockFailAt = Clock.Never;
                }
                return;
            }

            bool giveUp = false;
            long now = Clock.NowMs();
            lock (_sync)
            {
                if (hwnd != _blockFailHwnd || Clock.AgeMs(_blockFailAt) > 10000)
                {
                    _blockFailHwnd = hwnd;
                    _blockFailAt = now;
                    _blockFailCount = 0;
                }
                _blockFailCount++;
                if (_blockFailCount >= 5)
                {
                    // 放弃拦截只记"从什么时候开始"，时长由 SUPPRESS_MS 决定（见 IsSuppressed）
                    _suppressedFrom[hwnd] = now;
                    _blockFailCount = 0;
                    _blockFailAt = Clock.Never;
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

        /// <summary>
        /// 记一行日志。**只入队，不碰磁盘** —— 它会被 WinEvent 回调线程、钩子线程、动作线程调用，
        /// 同步 AppendAllText 在 Defender 扫盘时能把系统回调拖住几十毫秒。
        /// 行格式保持 `[yyyy-MM-dd HH:mm:ss.fff] 内容` 不变（测试脚本按这个前缀做正则）。
        /// </summary>
        public void Log(string message)
        {
            try
            {
                if (_logQueue.Count >= LOG_QUEUE_MAX)
                {
                    _logDropped++;
                    return;
                }
                string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] {1}", DateTime.Now, message);
                _logQueue.Enqueue(line);
                if (_logQueue.Count >= LOG_FLUSH_LINES) _logSignal.Set();
            }
            catch
            {
                // 日志写不进去不影响功能
            }
        }

        /// <summary>后台落盘线程：每 200ms 或攒够 64 行批量写一次；退出时把剩下的写完。</summary>
        private void LogWriterLoop()
        {
            while (!_disposed || !_logQueue.IsEmpty)
            {
                try
                {
                    _logSignal.WaitOne(LOG_FLUSH_INTERVAL_MS);
                    FlushLog();
                }
                catch
                {
                    // 写盘失败不能拖死线程
                }
            }
            FlushLog();
        }

        /// <summary>把队列里现有的行一次性写进文件。只有后台线程和 Dispose 会调。</summary>
        private void FlushLog()
        {
            if (_logQueue.IsEmpty) return;

            System.Text.StringBuilder sb = new System.Text.StringBuilder(4096);
            string line;
            while (_logQueue.TryDequeue(out line))
            {
                sb.Append(line).Append(Environment.NewLine);
            }
            if (sb.Length == 0) return;

            try
            {
                lock (_logLock)
                {
                    // 还是用 AppendAllText：行为和原来逐行写完全一致（含首次写文件时的 UTF-8 BOM），
                    // 只是现在一次写一批。
                    File.AppendAllText(_logPath, sb.ToString(), Encoding.UTF8);
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
            if (_switchHook != IntPtr.Zero)
            {
                Native.UnhookWinEvent(_switchHook);
                _switchHook = IntPtr.Zero;
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
            // 异步日志：等后台线程把最后一批写完（最多 500ms，超时也自己再刷一次，
            // 保证"最后一行"不会丢 —— 退出这条日志是排查问题时最需要的那条）。
            _logSignal.Set();
            try { _logThread.Join(500); }
            catch { }
            FlushLog();
            if (_logDropped > 0)
            {
                // 只在真的丢过行时提示一次（写到"退出"之后，不再入队）
                try
                {
                    lock (_logLock)
                    {
                        File.AppendAllText(_logPath,
                            string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] 提示：日志积压丢弃了 {1} 行（磁盘太慢）",
                                DateTime.Now, _logDropped) + Environment.NewLine,
                            Encoding.UTF8);
                    }
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// 单调时钟（毫秒）。存在的唯一理由：**不能用 DateTime.Now 算时间差**。
    ///
    /// 系统时钟被 NTP 校正、手动改时间、夏令时往回拨时，`DateTime.Now - _lastClickAt` 会变成负数，
    /// 于是 `ClickAgeMs > 350` 不成立 → "最近没按过键"这个判据永远不成立 → **守卫永久放行**；
    /// 同理"放弃拦截到几点"会变成无限期。这类 bug 只在改时间的那一刻出现，极难复现。
    ///
    /// Environment.TickCount 是开机以来的毫秒数，单调、只看差值、不受墙上时钟影响。
    /// 它是 32 位有符号、约 49.7 天回绕一次，所以统一转成 uint 语义的 long 存，
    /// 差值一律用 `unchecked((uint)(now - then))` 算 —— 只要间隔远小于 49.7 天就永远正确。
    /// 日志和界面上"几点几分"仍然用 DateTime.Now（只用于显示）。
    /// </summary>
    internal static class Clock
    {
        /// <summary>"从没发生过"的哨兵值（比任何真实 tick 都小）。</summary>
        public const long Never = -1;

        public static long NowMs()
        {
            return unchecked((uint)Environment.TickCount);
        }

        /// <summary>距今多少毫秒（回绕安全，结果永远 &gt;= 0）。Never 返回 double.MaxValue。</summary>
        public static double AgeMs(long thenMs)
        {
            if (thenMs < 0) return double.MaxValue;
            uint delta = unchecked((uint)((uint)Environment.TickCount - (uint)thenMs));
            // 超过 24.8 天只可能是"then 其实在未来"或真的隔了那么久：
            // 一律当"很久以前"，绝不返回负数（负数会让"350ms 内按过键"永远成立）。
            if (delta > 0x7FFFFFFFu) return double.MaxValue;
            return delta;
        }

        /// <summary>距今是否在 ms 毫秒之内。Never 恒为 false。</summary>
        public static bool Within(long thenMs, double ms)
        {
            return AgeMs(thenMs) <= ms;
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
