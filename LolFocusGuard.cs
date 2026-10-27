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

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int SW_MINIMIZE = 6;
        public const uint GW_OWNER = 4;
        public const uint GA_ROOT = 2;
        public const int VK_MENU = 0x12;
        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;

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
        private DateTime _lastClickAt = DateTime.MinValue;
        private string _lastClickProc = string.Empty;
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
        private const int INPUT_GRACE_MS = 500; // 最后一次输入后多久内算"用户操作引起"
        private IntPtr _mouseHook = IntPtr.Zero;
        private IntPtr _kbdHook = IntPtr.Zero;
        private Native.LowLevelHookProc _mouseProc;
        private Native.LowLevelHookProc _kbdProc;
        private DateTime _lastInputAt = DateTime.MinValue;
        private bool _lastInputIsKey;
        private string _lastInputWhere = string.Empty; // 鼠标按下时指针压在哪个进程的窗口上
        private volatile bool _altDown;

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
                Log(string.Format("输入钩子已安装（{0}ms 内的输入算你自己操作）", INPUT_GRACE_MS));
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
                    ProcessForeground(fg);
                }
                else if (fg != IntPtr.Zero && fg == _targetHwnd && _lastBlockAt != DateTime.MinValue &&
                         (DateTime.Now - _lastBlockAt).TotalMilliseconds > 1200)
                {
                    // 目标窗口一直赖在前台没被打回去：每隔 1.2 秒再试一次
                    _lastFgHwnd = IntPtr.Zero;
                    ProcessForeground(fg);
                }

                if (_verbose && (_pollTicks++ % 20) == 0)
                {
                    Log("心跳 fg=" + ProcNameOf(fg));
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

        private static bool UnusedIsMouseButtonDown()
        {
            const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_XBUTTON1 = 0x05, VK_XBUTTON2 = 0x06;
            return (Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
                || (Native.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0
                || (Native.GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0
                || (Native.GetAsyncKeyState(VK_XBUTTON1) & 0x8000) != 0
                || (Native.GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
        }

        /// <summary>鼠标指针下面那个顶层窗口属于哪个进程。</summary>
        private static string ProcUnderCursor()
        {
            Native.POINT pt;
            if (!Native.GetCursorPos(out pt)) return string.Empty;
            IntPtr under = Native.WindowFromPoint(pt);
            if (under == IntPtr.Zero) return string.Empty;
            IntPtr root = Native.GetAncestor(under, Native.GA_ROOT);
            return ProcNameOf(root == IntPtr.Zero ? under : root);
        }

        private static bool IsShellProcess(string proc)
        {
            return string.Equals(proc, "explorer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "StartMenuExperienceHost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "SearchHost", StringComparison.OrdinalIgnoreCase);
        }

        // ---------- LCU 登录状态（只在本机 127.0.0.1 上调客户端自己的接口，不注入） ----------
        private static bool IsClientProcess(string proc)
        {
            return string.Equals(proc, "LeagueClientUx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "LeagueClient", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "RiotClientServices", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWeGameProcess(string proc)
        {
            return string.Equals(proc, "wegame", StringComparison.OrdinalIgnoreCase)
                || string.Equals(proc, "wegame_env", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>客户端进程在不在（WeGame 登录窗口只在客户端起来之前出现，用它当判据）。</summary>
        private void RefreshClientRunning()
        {
            if ((DateTime.Now - _clientCheckAt).TotalMilliseconds < 800) return;
            _clientCheckAt = DateTime.Now;

            bool running = false;
            string[] names = { "LeagueClientUx", "LeagueClient" };
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
            ProcessForeground(hwnd);
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

        private void ProcessForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            if (hwnd == _lastFgHwnd) return; // 这次切换已经处理过

            lock (_sync)
            {
                if (hwnd == _lastFgHwnd) return;
                _lastFgHwnd = hwnd;

                string proc = ProcNameOfCached(hwnd);
                bool isTarget = _targets.Contains(proc);

                if (_verbose)
                {
                    Native.RECT vr;
                    string size = Native.GetWindowRect(hwnd, out vr)
                        ? string.Format("{0}x{1}@({2},{3})", vr.Right - vr.Left, vr.Bottom - vr.Top, vr.Left, vr.Top)
                        : "?";
                    Log(string.Format("前台变化 → {0} (pid={1}) class={2} 标题=\"{3}\" 尺寸={4} 最小化={5} 有输入={6}({7})",
                        proc, ProcIdOf(hwnd), ClassNameOf(hwnd), TitleOf(hwnd), size,
                        Native.IsIconic(hwnd),
                        (DateTime.Now - _lastInputAt).TotalMilliseconds <= INPUT_GRACE_MS, _lastInputWhere));
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

                // 国服的登录窗口是 WeGame 弹的（不是客户端）：客户端还没起来的时候，
                // WeGame 的窗口一律放行，不然用户连登录都点不了；
                // 客户端已经起来之后再冒出来的（"游戏进程已启动"那张卡片）才拦。
                if (IsWeGameProcess(proc) && !_clientRunning)
                {
                    if (_verbose) Log("放行原因: WeGame 登录阶段（客户端还没起来）");
                    else if ((DateTime.Now - _lastWegameAllowLogAt).TotalSeconds > 3)
                    {
                        // 非 verbose 下也留一条证据，方便排查"到底放行了哪个窗口"
                        _lastWegameAllowLogAt = DateTime.Now;
                        Log(string.Format("放行 WeGame 窗口（登录阶段，客户端还没起来）：class={0}，标题=\"{1}\"",
                            ClassNameOf(hwnd), TitleOf(hwnd)));
                    }
                    return;
                }

                // 客户端第一次冒出窗口（登录页 / 启动页 / 安全检测）：放行。
                // 之后同一个窗口再跳前台（匹配成功那类）就按输入判据处理了。
                if (IsFirstClientWindow(hwnd, proc))
                {
                    if (_verbose) Log("放行原因: 客户端窗口第一次出现（登录页/启动页）");
                    return;
                }

                if (ShouldAllow(hwnd)) return;

                Block(hwnd);
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
                    _lastInputIsKey = false;
                    _lastInputAt = DateTime.Now;
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
                        _lastInputIsKey = true;
                        _lastInputAt = DateTime.Now;
                        if (info.vkCode == 0x12) _altDown = true; // VK_MENU
                    }
                    else if (msg == 0x0101 || msg == 0x0105) // KEYUP / SYSKEYUP
                    {
                        if (info.vkCode == 0x12) _altDown = false;
                    }
                }
                catch
                {
                }
            }
            return Native.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
        }

        /// <summary>判断这次前台切换是不是用户自己弄出来的。</summary>
        private bool ShouldAllow(IntPtr hwnd)
        {
            if (_noClickAllow) return false; // 测试模式：忽略一切输入放行

            // Alt 按着（Alt+Tab）→ 放行
            if (_altDown || (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0)
            {
                if (_verbose) Log("放行原因: Alt 按着（Alt+Tab）");
                return true;
            }

            double idle = (DateTime.Now - _lastInputAt).TotalMilliseconds;

            // 最近有键盘输入 → 是你在切窗口（Alt+Tab、Win+数字、Alt+F4 之类）
            if (idle <= INPUT_GRACE_MS && _lastInputIsKey)
            {
                if (_verbose) Log(string.Format("放行原因: {0}ms 前有键盘输入", (int)idle));
                return true;
            }

            // 最近有鼠标点击，且按下的位置在被切过去的这个窗口自己身上 → 是你点的
            if (idle <= INPUT_GRACE_MS && !_lastInputIsKey && _lastInputWhere.Length > 0)
            {
                if (_targets.Contains(_lastInputWhere))
                {
                    if (_verbose) Log("放行原因: 你点了这个窗口（" + _lastInputWhere + "）");
                    return true;
                }
                if (IsShellProcess(_lastInputWhere))
                {
                    if (_verbose) Log("放行原因: 你点了任务栏/开始菜单（" + _lastInputWhere + "）");
                    return true;
                }
                // 点在别的程序上（你正在打字/点游戏），这个窗口却跑到前台来了 = 它自己弹的
            }

            // 注意：这里刻意**不**用"指针此刻压在目标窗口上"放行。
            // 鼠标可能只是随手停在那里（比如刚才点过它），窗口自己弹出来时会被误放行。
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
            _lastBlockAt = now;

            int number = ++_blockCount;

            // 同一窗口在 2 秒内又来抢前台 → 说明只把焦点还回去不够（它自己还会再弹），
            // 直接最小化掉，别跟它来回拉锯。
            bool escalating = (hwnd == _lastBlockedHwnd) &&
                              (now - _lastBlockAt).TotalMilliseconds < 2000;
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
                    if (wasMinimized)
                    {
                        Native.ShowWindow(hwnd, SW_MINIMIZE);
                        System.Threading.Thread.Sleep(5);
                    }
                    // 最小化后系统一般会把前台交给下一个窗口；没交就自己交还
                    if (Native.GetForegroundWindow() == hwnd && restore != IntPtr.Zero)
                    {
                        ForceForeground(restore);
                        System.Threading.Thread.Sleep(10);
                        if (Native.GetForegroundWindow() == hwnd)
                        {
                            Native.SwitchToThisWindow(restore, false); // 最后的兜底
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("拦截失败: " + ex.Message);
                }
            });
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
        [STAThread]
        private static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); }
            catch { }

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
