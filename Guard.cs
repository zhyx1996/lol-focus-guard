using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace LolFocusGuard
{
    internal sealed class SystemFocusClock : IFocusClock
    { public long NowMs { get { return Clock.NowMs(); } } }

    internal sealed class Guard : IDisposable, IFocusWindows, IFocusActions
    {
        private readonly HashSet<string> _targets;
        private readonly bool _watchWeGame;
        private static readonly int OwnPid = Process.GetCurrentProcess().Id;
        private readonly string _logPath, _lcuDir;
        private readonly bool _verbose;
        private readonly FocusEngine _engine;
        private readonly object _identityGate = new object();
        private readonly Dictionary<IntPtr, WindowId> _identities = new Dictionary<IntPtr, WindowId>();
        private long _generation;
        private readonly object _foregroundCacheGate = new object();
        private WindowInfo _foregroundCache;
        private long _foregroundCacheAt = Clock.Never;
        private readonly Native.WinEventDelegate _callback;
        private readonly Native.LowLevelHookProc _mouseProc, _kbdProc;
        private IntPtr _hook, _switchHook, _destroyHook, _mouseHook, _kbdHook;
        private readonly Thread _pollThread, _inputThread, _actionThread, _logThread;
        private volatile bool _disposed;
        private readonly AutoResetEvent _actionSignal = new AutoResetEvent(false);
        private DateTime _resumeAt = DateTime.MinValue;
        private long _pauseStartedAtMs = Clock.Never, _pauseDurationMs;
        private volatile bool _clientRunning;
        private long _clientCheckAt = Clock.Never, _lastAliveLogAt = Clock.NowMs(), _lastDiagnosticAt = Clock.Never;
        private int _blockCountAtClientStart, _loginState;
        private int _switchCount;
        private long _lastVerboseHeartbeatAt = Clock.Never;
        private bool _loginStateLogged;
        private string _lcuLockfile;
        private readonly object _keyboardGate = new object();
        private readonly bool[] _keys = new bool[256];
        private readonly long[] _keyHookAt = new long[256];
        private static readonly int[] SwitchKeys = new int[] { 9, 27, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x44, 0x4d, 0x25, 0x26, 0x27, 0x28 };
        private static readonly string[] ShellClasses = new string[]
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
            "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame",
            "XamlExplorerHostIslandWindow", "Xaml_WindowedPopupClass"
        };
        private const long LOG_ROTATE_BYTES = 8L * 1024 * 1024;
        private const int LOG_FLUSH_INTERVAL_MS = 200, LOG_FLUSH_LINES = 64, LOG_QUEUE_MAX = 4096;
        private readonly object _logLock = new object();
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
        private readonly AutoResetEvent _logSignal = new AutoResetEvent(false);
        private int _logDropped;

        public Guard(IEnumerable<string> targets, string logPath, bool verbose, bool noClickAllow, string lcuDir)
        {
            _targets = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);
            _watchWeGame = _targets.Contains("wegame") || _targets.Contains("wegame_env");
            _logPath = logPath; _lcuDir = lcuDir; _verbose = verbose;
            for (int i = 0; i < _keyHookAt.Length; i++) _keyHookAt[i] = Clock.Never;
            _engine = new FocusEngine(new SystemFocusClock(), this, this, noClickAllow, EngineLog);
            RefreshClientRunning();
            _engine.Initialize(((IFocusWindows)this).Foreground);
            _callback = OnWindowEvent; _mouseProc = MouseHookProc; _kbdProc = KeyboardHookProc;
            uint flags = Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS;
            _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _callback, 0, 0, flags);
            _switchHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_SWITCHSTART, Native.EVENT_SYSTEM_SWITCHEND, IntPtr.Zero, _callback, 0, 0, flags);
            _destroyHook = Native.SetWinEventHook(0x8001, 0x8001, IntPtr.Zero, _callback, 0, 0, flags);
            IntPtr module = Native.GetModuleHandle(null);
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
            _kbdHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _kbdProc, module, 0);
            _logThread = StartThread(LogWriterLoop, "LolFocusGuard-log", ThreadPriority.BelowNormal);
            _inputThread = StartThread(InputLoop, "LolFocusGuard-input", ThreadPriority.AboveNormal);
            _pollThread = StartThread(PollLoop, "LolFocusGuard-poll", ThreadPriority.Normal);
            _actionThread = StartThread(ActionLoop, "LolFocusGuard-action", ThreadPriority.AboveNormal);
            Log("守卫启动，监控进程: " + string.Join(", ", _targets));
            Log("判定依据: 一次性切换凭据 + 窗口身份；主动切走立即撤销授权；15ms 输入采样兜底");
            if (_hook == IntPtr.Zero) Log("警告：前台事件钩子安装失败，使用轮询兜底");
            if (_mouseHook == IntPtr.Zero) Log("警告：鼠标钩子不可用，使用轮询输入；极短点击可能漏采");
        }
        private static Thread StartThread(ThreadStart action, string name, ThreadPriority priority)
        {
            Thread thread = new Thread(action); thread.IsBackground = true; thread.Name = name; thread.Priority = priority; thread.Start(); return thread;
        }
        public int BlockCount { get { return _engine.BlockCount; } }
        public bool Paused { get { return _engine.Paused; } }
        public DateTime ResumeAt { get { return _resumeAt; } }
        public string LogPath { get { return _logPath; } }
        public void Pause(TimeSpan duration)
        {
            _pauseStartedAtMs = Clock.NowMs(); _pauseDurationMs = (long)duration.TotalMilliseconds;
            _resumeAt = DateTime.Now.Add(duration); _engine.Pause(true); _actionSignal.Set();
            Log("已暂停至 " + _resumeAt.ToString("HH:mm:ss"));
        }
        public void Resume() { _engine.Pause(false); _resumeAt = DateTime.MinValue; Log("已恢复运行"); }
        public void OnSessionChanged(bool unlocked)
        { _engine.Session(!unlocked); _actionSignal.Set(); Log(unlocked ? "会话已解锁（恢复窗口保护 2 秒）" : "会话已锁定（取消窗口动作）"); }
        private void EngineLog(string line)
        {
            if (line.StartsWith("放行原因: WeGame 登录阶段")) Log("放行 WeGame 窗口（登录阶段，客户端还没起来）");
            if (line.StartsWith("放行原因: 刚解锁")) Log("放行（不是拦截）：刚解锁/恢复会话");
            if (_verbose || line.StartsWith("拦截 #") || line.StartsWith("放弃拦截")) Log(line);
        }
        private void ActionLoop()
        {
            while (!_disposed)
            {
                try
                {
                    _engine.Tick();
                    BlockRequest request = _engine.TakeReady();
                    if (request != null) _engine.Execute(request);
                    else _actionSignal.WaitOne(5);
                }
                catch (Exception ex) { Log("动作失败: " + ex.Message); _actionSignal.WaitOne(10); }
            }
        }
        private void PollLoop()
        {
            int tick = 0;
            while (!_disposed)
            {
                try
                {
                    if (Paused && Clock.AgeMs(_pauseStartedAtMs) >= _pauseDurationMs) Resume();
                    RefreshClientRunning();
                    if ((tick++ % 4) == 0)
                    {
                        Native.EnumWindows(delegate(IntPtr hwnd, IntPtr param)
                        {
                            if (Native.IsIconic(hwnd)) _engine.RememberMinimized(Inspect(hwnd));
                            return !_disposed;
                        }, IntPtr.Zero);
                    }
                    if (Clock.AgeMs(_lastDiagnosticAt) >= 2000)
                    { _lastDiagnosticAt = Clock.NowMs(); RefreshLoginState(); }
                    if (Clock.AgeMs(_lastAliveLogAt) >= 1800000)
                    { _lastAliveLogAt = Clock.NowMs(); Log("仍在运行，累计拦截 " + BlockCount + " 次"); }
                    if (_verbose && Clock.AgeMs(_lastVerboseHeartbeatAt) >= 2000)
                    {
                        _lastVerboseHeartbeatAt = Clock.NowMs();
                        Log("心跳 累计拦截=" + BlockCount + "次 切换器事件=" + Interlocked.CompareExchange(ref _switchCount, 0, 0) + "次");
                    }
                }
                catch (Exception ex) { if (_verbose) Log("状态采集失败: " + ex.Message); }
                Thread.Sleep(50);
            }
        }
        private void OnWindowEvent(IntPtr hook, uint type, IntPtr hwnd, int obj, int child, uint thread, uint at)
        {
            if (_disposed) return;
            try
            {
                if (type == 0x8001)
                {
                    if (obj != 0 || child != 0) return;
                    lock (_identityGate) _identities.Remove(hwnd);
                    _engine.Destroy(hwnd);
                }
                else if (type == Native.EVENT_SYSTEM_SWITCHSTART)
                { Interlocked.Increment(ref _switchCount); _engine.SwitchGesture(InputSource.SystemSwitch); }
                else if (type == Native.EVENT_SYSTEM_SWITCHEND) _engine.SwitchEnded();
                else if (hwnd != IntPtr.Zero && Native.GetForegroundWindow() == hwnd) _engine.Foreground(Inspect(hwnd));
                _actionSignal.Set();
            }
            catch (Exception ex) { if (_verbose) Log("窗口事件采集失败: " + ex.Message); }
        }
        private static bool Down(int key) { return (Native.GetAsyncKeyState(key) & 0x8000) != 0; }
        private void InputLoop()
        {
            WindowInfo previousHit = null;
            Native.POINT previousPoint = new Native.POINT();
            WindowId previousForeground = new WindowId();
            bool[] previousDown = new bool[3];
            int[] buttons = new int[] { 1, 2, 4 };
            while (!_disposed)
            {
                try
                {
                    Native.POINT point;
                    if (Native.GetCursorPos(out point))
                    {
                        WindowInfo hit = HitAt(point);
                        WindowInfo fg = ((IFocusWindows)this).Foreground;
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            bool down = Down(buttons[i]);
                            WindowInfo sampledHit = hit;
                            // When a popup covers a stationary pointer between polls, the
                            // last pre-down hit is better evidence than the new popup.
                            if (down && !previousDown[i] && previousHit != null && fg != null && fg.Id != previousForeground &&
                                point.X == previousPoint.X && point.Y == previousPoint.Y) sampledHit = previousHit;
                            _engine.Button(i, down, sampledHit, point.X, point.Y, InputSource.Poll);
                            previousDown[i] = down;
                        }
                        previousPoint = point; previousHit = hit;
                        if (fg != null) previousForeground = fg.Id;
                    }
                    foreach (int key in SwitchKeys) FeedKey(key, Down(key), InputSource.Poll);
                    _actionSignal.Set();
                }
                catch (Exception ex) { if (_verbose) Log("输入采集失败: " + ex.Message); }
                Thread.Sleep(15);
            }
        }
        private void FeedKey(int key, bool down, InputSource source)
        {
            if (key < 0 || key >= 256) return;
            lock (_keyboardGate)
            {
                if (source == InputSource.Poll && Clock.AgeMs(_keyHookAt[key]) < 30) return;
                if (source == InputSource.Hook) _keyHookAt[key] = Clock.NowMs();
                bool edge = down && !_keys[key]; _keys[key] = down;
                if (!edge) return;
                bool alt = Down(Native.VK_MENU), win = Down(Native.VK_LWIN) || Down(Native.VK_RWIN);
                bool ctrl = Down(0x11);
                if (FocusRules.IsKeyboardSwitch(key, alt, win, ctrl)) _engine.SwitchGesture(InputSource.Keyboard);
            }
        }
        private IntPtr MouseHookProc(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0 && !_disposed)
            {
                int msg = wp.ToInt32();
                int button = msg == 0x201 || msg == 0x202 ? 0 : msg == 0x204 || msg == 0x205 ? 1 : msg == 0x207 || msg == 0x208 ? 2 : -1;
                if (button >= 0)
                {
                    try
                    {
                        Native.MSLLHOOKSTRUCT info = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(Native.MSLLHOOKSTRUCT));
                        bool down = msg == 0x201 || msg == 0x204 || msg == 0x207;
                        _engine.Button(button, down, down ? HitAt(info.pt) : null, info.pt.X, info.pt.Y, InputSource.Hook);
                        _actionSignal.Set();
                    }
                    catch { }
                }
            }
            return Native.CallNextHookEx(_mouseHook, code, wp, lp);
        }
        private IntPtr KeyboardHookProc(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0 && !_disposed)
            {
                try
                {
                    Native.KBDLLHOOKSTRUCT info = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lp, typeof(Native.KBDLLHOOKSTRUCT));
                    int msg = wp.ToInt32();
                    if (msg == 0x100 || msg == 0x104 || msg == 0x101 || msg == 0x105)
                        FeedKey((int)info.vkCode, msg == 0x100 || msg == 0x104, InputSource.Hook);
                    _actionSignal.Set();
                }
                catch { }
            }
            return Native.CallNextHookEx(_kbdHook, code, wp, lp);
        }
        private static bool ShellClass(string cls)
        { foreach (string entry in ShellClasses) if (string.Equals(entry, cls, StringComparison.OrdinalIgnoreCase)) return true; return false; }
        private WindowInfo HitAt(Native.POINT point)
        {
            IntPtr under = Native.WindowFromPoint(point), current = under;
            for (int i = 0; i < 12 && current != IntPtr.Zero; i++)
            {
                if (ShellClass(ClassNameOf(current)))
                {
                    WindowInfo entry = Inspect(current);
                    if (entry != null && entry.Shell) return entry;
                }
                IntPtr parent = Native.GetParent(current);
                if (parent == current) break;
                current = parent;
            }
            IntPtr root = Native.GetAncestor(under, Native.GA_ROOT);
            return Inspect(root == IntPtr.Zero ? under : root);
        }

        WindowInfo IFocusWindows.Foreground
        {
            get
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                lock (_foregroundCacheGate)
                {
                    if (_foregroundCache != null && _foregroundCache.Id.Handle == hwnd && Clock.AgeMs(_foregroundCacheAt) < 15)
                        return _foregroundCache;
                    _foregroundCache = Inspect(hwnd); _foregroundCacheAt = Clock.NowMs();
                    return _foregroundCache;
                }
            }
        }
        public WindowInfo Inspect(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return null;
            string proc;
            double age;
            WindowId id = Identify(hwnd, out proc, out age);
            if (id.Empty) return null;
            string title = TitleOf(hwnd), cls = ClassNameOf(hwnd);
            bool wegame = ProcClass.IsWeGameWindow(proc, title, cls);
            bool target = ProcClass.IsTarget(proc, title, cls, _targets.Contains(proc), _watchWeGame);
            bool shell = ProcClass.IsShell(proc) && ShellClass(cls);
            var owners = new List<WindowId>();
            IntPtr owner = Native.GetWindow(hwnd, Native.GW_OWNER);
            for (int i = 0; i < 12 && owner != IntPtr.Zero && owner != hwnd; i++)
            {
                string name; double ownerAge;
                WindowId ownerId = Identify(owner, out name, out ownerAge);
                if (ownerId.Empty || owners.Contains(ownerId)) break;
                owners.Add(ownerId); owner = Native.GetWindow(owner, Native.GW_OWNER);
            }
            Native.RECT rect;
            string size = Native.GetWindowRect(hwnd, out rect)
                ? string.Format("{0}x{1}@({2},{3})", rect.Right - rect.Left, rect.Bottom - rect.Top, rect.Left, rect.Top) : "?";
            return new WindowInfo(id, proc, title, cls, target, shell,
                id.Pid == OwnPid, Native.IsIconic(hwnd),
                wegame && !_clientRunning, ProcClass.IsClient(proc), age, owners.ToArray(), size);
        }
        private WindowId Identify(IntPtr hwnd, out string process, out double age)
        {
            process = string.Empty; age = -1;
            uint pid; uint thread = Native.GetWindowThreadProcessId(hwnd, out pid);
            if (thread == 0 || pid == 0) return new WindowId();
            long birth = 0, exit, kernel, user;
            IntPtr handle = Native.OpenProcess(0x1000, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    if (Native.GetProcessTimes(handle, out birth, out exit, out kernel, out user))
                        age = Math.Max(0, (DateTime.UtcNow.ToFileTimeUtc() - birth) / 10000.0);
                    StringBuilder path = new StringBuilder(1024); int size = path.Capacity;
                    if (Native.QueryFullProcessImageName(handle, 0, path, ref size)) process = Path.GetFileNameWithoutExtension(path.ToString());
                }
                finally { Native.CloseHandle(handle); }
            }
            if (process.Length == 0) process = ProcNameOfPid(pid);
            lock (_identityGate)
            {
                WindowId prior;
                if (_identities.TryGetValue(hwnd, out prior) && prior.Pid == (int)pid && prior.Thread == thread && prior.Birth == birth) return prior;
                // Prune dead handles rather than resetting live generations (which would
                // invalidate active user authorizations when unrelated windows are created).
                if (_identities.Count >= 1024)
                {
                    var dead = new List<IntPtr>();
                    foreach (IntPtr key in _identities.Keys) if (!Native.IsWindow(key)) dead.Add(key);
                    foreach (IntPtr key in dead) _identities.Remove(key);
                }
                WindowId id = new WindowId(hwnd, (int)pid, thread, birth, ++_generation);
                _identities[hwnd] = id; return id;
            }
        }
        private void DiagnoseModal(IntPtr hwnd)
        {
            if (_verbose && !Native.IsWindowEnabled(hwnd))
                Log("诊断：这个窗口打回时不可交互（IsWindowEnabled=False，模态子窗口=" +
                    Native.GetWindow(hwnd, Native.GW_ENABLEDPOPUP) + "）——可能正在显示必须点的弹窗");
        }
        void IFocusActions.Minimize(IntPtr hwnd) { DiagnoseModal(hwnd); Native.ShowWindowAsync(hwnd, Native.SW_MINIMIZE); }
        void IFocusActions.PostMinimize(IntPtr hwnd) { Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MINIMIZE, IntPtr.Zero); }
        void IFocusActions.Restore(IntPtr hwnd, bool fallback)
        {
            if (fallback) { Native.SwitchToThisWindow(hwnd, false); return; }
            IntPtr from = Native.GetForegroundWindow();
            WindowInfo current = Inspect(from);
            if (current != null && current.Target) DiagnoseModal(from);
            if (Native.IsIconic(hwnd)) Native.ShowWindowAsync(hwnd, 9);
            uint pid; uint targetThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            uint currentThread = Native.GetCurrentThreadId(); bool attached = false;
            try
            {
                if (targetThread != 0 && targetThread != currentThread) attached = Native.AttachThreadInput(currentThread, targetThread, true);
                Native.SetForegroundWindow(hwnd);
            }
            finally { if (attached) Native.AttachThreadInput(currentThread, targetThread, false); }
        }
        void IFocusActions.Wait(int ms) { Thread.Sleep(ms); }

        // Existing LCU diagnostics, process helpers and asynchronous rotating log follow.
        private static bool IsClientProcess(string name) { return ProcClass.IsClient(name); }
        private void RefreshClientRunning()
        {
            if (Clock.AgeMs(_clientCheckAt) < 800) return;
            _clientCheckAt = Clock.NowMs();
            bool running = false;
            foreach (string name in new string[] { "LeagueClientUx", "LeagueClient", "RiotClientServices" })
            {
                Process[] processes = Process.GetProcessesByName(name);
                running |= processes.Length > 0;
                foreach (Process process in processes) process.Dispose();
            }
            bool previous = _clientRunning; _clientRunning = running;
            if (previous == running) return;
            if (running)
            {
                _blockCountAtClientStart = BlockCount;
                Log("监测到客户端/WeGame 已启动：累计拦截 " + BlockCount + " 次");
            }
            else Log("客户端/WeGame 已全部退出：本轮拦截 " + (BlockCount - _blockCountAtClientStart) + " 次，累计 " + BlockCount + " 次");
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
            try
            {
                if (_logQueue.Count >= LOG_QUEUE_MAX)
                {
                    Interlocked.Increment(ref _logDropped);
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
                    // 日志无上限地长下去迟早出问题（长时间开 --verbose 一天能写几十 MB，
                    // 用户也不会自己去清）。超过上限就轮转一次：guard.log → guard.log.1（覆盖上一份），
                    // 一份 8MB 够存几万行，排查用最近这份就够了。
                    try
                    {
                        FileInfo fi = new FileInfo(_logPath);
                        if (fi.Exists && fi.Length > LOG_ROTATE_BYTES)
                        {
                            string backup = _logPath + ".1";
                            if (File.Exists(backup)) File.Delete(backup);
                            File.Move(_logPath, backup);
                        }
                    }
                    catch
                    {
                        // 轮转失败就继续往原文件写，不影响功能
                    }
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
            if (_disposed) return;
            _engine.Stop();
            _disposed = true;
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
            if (_switchHook != IntPtr.Zero) Native.UnhookWinEvent(_switchHook);
            if (_destroyHook != IntPtr.Zero) Native.UnhookWinEvent(_destroyHook);
            if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
            if (_kbdHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_kbdHook);
            _actionSignal.Set();
            foreach (Thread thread in new Thread[] { _inputThread, _pollThread, _actionThread })
                if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
            Log("守卫退出");
            _logSignal.Set(); _logThread.Join(1000); FlushLog();
            if (_logDropped > 0)
            {
                try
                {
                    lock (_logLock) File.AppendAllText(_logPath, "提示：日志积压丢弃了 " + _logDropped + " 行" + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
    }
}
