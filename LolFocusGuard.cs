// LolFocusGuard —— 从系统层面拦住英雄联盟客户端"自己弹到前台抢焦点"
//
// 原理：用 SetWinEventHook 监听全局前台窗口切换（EVENT_SYSTEM_FOREGROUND）。
// 当英雄联盟客户端窗口成为前台、且判定为"没人点它"时：
//   · 若它本来是最小化的，立刻再最小化回去；
//   · 把前台还给之前那个窗口（AttachThreadInput + SetForegroundWindow）。
// 全程不注入客户端、不读客户端内存、不改客户端文件。
//
// 编译：pwsh ./build.ps1 -SkipStop -OutputDirectory ./artifacts
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

        public const uint GW_ENABLEDPOPUP = 6;

        [DllImport("user32.dll")]
        public static extern bool IsWindowEnabled(IntPtr hWnd);

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
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

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

        // ---- 会话通知（锁屏/解锁）：wtsapi32.dll，挂在离屏 HotkeyWindow 上收窗口消息 ----
        // 用来修"解锁后 Windows 把前台还给客户端 → 被当成它自己弹出来"这条误判。
        public const int WM_WTSSESSION_CHANGE = 0x02B1;
        public const int WTS_SESSION_LOCK = 0x7;
        public const int WTS_SESSION_UNLOCK = 0x8;
        public const int NOTIFY_FOR_THIS_SESSION = 0;

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSRegisterSessionNotification(IntPtr hWnd, int dwFlags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

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

    /// <summary>用来接收全局热键 + 会话通知（锁屏/解锁）的隐藏窗口。</summary>
    internal sealed class HotkeyWindow : Form
    {
        public event Action HotkeyPressed;

        /// <summary>会话状态变化：参数 true = 解锁/恢复会话，false = 锁屏。</summary>
        public event Action<bool> SessionChanged;

        // 已经注册会话通知的那个 hwnd（注销时用它，避免在销毁途中读 Handle 属性把窗口重建出来）
        private IntPtr _sessionNotifyHwnd = IntPtr.Zero;

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

        /// <summary>
        /// 注册会话通知（锁屏/解锁）。宿主就是这个窗口 —— 它已经有一个在收消息的隐藏 hwnd，
        /// 不用再新开一个窗口。返回 false = 这台机器上挂不上（个别精简系统 / 服务会话），
        /// 调用方写一行日志即可，功能照旧（只是没有"解锁后别压回去"这条兜底）。
        /// </summary>
        public bool RegisterSessionNotify()
        {
            try
            {
                IntPtr h = Handle; // 确保句柄已创建（TrayApp 里 Show() 之后调）
                if (!Native.WTSRegisterSessionNotification(h, Native.NOTIFY_FOR_THIS_SESSION)) return false;
                _sessionNotifyHwnd = h; // 记下来：注销时不再碰 Handle 属性（那会在销毁途中重建窗口）
                return true;
            }
            catch
            {
                return false; // wtsapi32.dll 缺失之类：绝不能因为兜底功能把程序搞崩
            }
        }

        private void UnregisterSessionNotify()
        {
            if (_sessionNotifyHwnd == IntPtr.Zero) return;
            try
            {
                Native.WTSUnRegisterSessionNotification(_sessionNotifyHwnd);
            }
            catch
            {
            }
            _sessionNotifyHwnd = IntPtr.Zero; // 幂等：重复调用不会重复注销
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            // 窗口销毁前必须注销，否则系统会把消息发给一个已经没了的 hwnd。
            // 只用缓存的句柄，不读 Handle 属性 —— 在 OnHandleDestroyed 里读它可能把窗口重建出来。
            UnregisterSessionNotify();
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && HotkeyPressed != null)
            {
                HotkeyPressed();
                return;
            }
            // 锁屏 / 解锁：只认这两个，别的会话事件（远程连接、控制台切换…）不关心
            if (m.Msg == Native.WM_WTSSESSION_CHANGE)
            {
                int code = m.WParam.ToInt32();
                if (code == Native.WTS_SESSION_UNLOCK || code == Native.WTS_SESSION_LOCK)
                {
                    if (SessionChanged != null) SessionChanged(code == Native.WTS_SESSION_UNLOCK);
                    return;
                }
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

            // 锁屏/解锁通知：挂在同一个离屏窗口上（和热键共用一个 hwnd）。
            // 挂不上不算错误，只少一条"解锁后别把窗口压回去"的兜底。
            _hotkeyWindow.SessionChanged += delegate(bool unlocked) { _guard.OnSessionChanged(unlocked); };
            if (!_hotkeyWindow.RegisterSessionNotify())
            {
                _guard.Log("会话通知注册失败（解锁时可能会把窗口误判成自弹）");
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
                else if (line.IndexOf("放弃拦截 15 秒", StringComparison.Ordinal) >= 0)
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
                    // 日志措辞改过两次，这里按现在的两种写法匹配（旧的"没有输入"留着兼容老日志）
                    if (why.IndexOf("没有尚未用掉的鼠标按键", StringComparison.Ordinal) >= 0 ||
                        why.IndexOf("内没有鼠标按键按下", StringComparison.Ordinal) >= 0 ||
                        why.IndexOf("没有输入", StringComparison.Ordinal) >= 0) noInputCount++;
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
            sb.AppendLine(string.Format("放弃拦截（防拉锯：10 秒内 5 次压不动就停手 15 秒）: {0} 次", giveUpCount));
            sb.AppendLine(string.Format("判为自弹的依据:「一直没有输入」{0} 次，「输入落在别的程序上」{1} 次",
                noInputCount, inputElsewhereCount));
            if (blockCount > 0 && noInputCount + inputElsewhereCount == 0)
            {
                // 「不放行: …」这类依据只在 --verbose 下才写日志，普通日志里数不出来；
                // 不说明的话这一栏永远显示 0 次，看着像统计坏了。
                sb.AppendLine("  （这一栏要看 --verbose 日志；普通日志只记『拦截 #N』）");
            }

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
                targets.Add("LeagueClientUx");
                targets.Add("LeagueClient");   // 英雄联盟客户端主窗口
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
