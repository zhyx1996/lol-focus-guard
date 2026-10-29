// 测试用的"假客户端"：反复做"自己弹到前台"的动作，用来验证 LolFocusGuard
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    private static string _logPath;

    private sealed class FakeClient : Form
    {
        private readonly Timer _timer;
        private readonly bool _quiet;
        private bool _minimizedOnce;
        private readonly bool _modal;
        private readonly bool _stayMinimized;

        /// <summary>
        /// quiet = 不自动弹（窗口就静静待着）。给"需要确定前台顺序"的测试用：
        /// 自动弹的 FakeClient 每秒抢一次前台，Alt+Tab 的 MRU 顺序根本没法控制。
        /// </summary>
        public FakeClient(bool quiet, int intervalMs, bool modal, bool stayMinimized)
        {
            _quiet = quiet;
            _modal = modal;
            _stayMinimized = stayMinimized;
            if (_stayMinimized) WindowState = FormWindowState.Minimized;
            Text = "FakeClient";
            ClientSize = new Size(420, 260);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(200, 150);

            _timer = new Timer();
            _timer.Interval = intervalMs;
            _timer.Tick += OnTick;
            if (_quiet) _timer.Enabled = false;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // modal 模式：把自己禁掉（EnableWindow(false)），这样 IsWindowEnabled=False ——
            // 守卫会把它当成"带着模态弹窗的窗口"，用来验证"只抢前台、不最小化"那条路。
            if (_modal)
            {
                EnableWindow(Handle, false);
                Log("modal 模式：窗口已禁用（IsWindowEnabled=False），模拟带模态弹窗");
            }
            if (!_quiet) _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                // minimized 模式：保持最小化，却把 SetForegroundWindow 打在自己的（图标状态）窗口上 ——
                // 真机 22:17:18 的 `尺寸=237x39@(-32000,-32000) 最小化=True` 就是这个动作。
                // 守卫应该回一句"放行原因: 它自己就是最小化状态"，而不是把它打回。
                if (_stayMinimized)
                {
                    SetForegroundWindow(Handle);
                    Log("raise-while-minimized ok=" + (GetForegroundWindow() == Handle));
                    return;
                }
                bool iconic = IsIconic(Handle);
                if (iconic)
                {
                    // 模拟客户端"从后台弹出来抢前台"
                    ShowWindow(Handle, SW_RESTORE);
                    SetForegroundWindow(Handle);
                    Log("pop（自己弹到前台）ok=" + (GetForegroundWindow() == Handle));
                }
                else if (_minimizedOnce)
                {
                    ShowWindow(Handle, SW_MINIMIZE);
                    Log("minimize（回到后台）");
                }
                _minimizedOnce = true;
            }
            catch (Exception ex)
            {
                Log("error: " + ex.Message);
            }
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath,
                string.Format("[{0:HH:mm:ss.fff}] {1}{2}", DateTime.Now, message, Environment.NewLine));
        }
        catch
        {
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        _logPath = Path.Combine(Path.GetTempPath(), "fakeclient.log");
        try { File.Delete(_logPath); }
        catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Log("启动");
        bool quiet = args.Length > 0 && string.Equals(args[0], "quiet", StringComparison.OrdinalIgnoreCase);
        // fast 模式：80ms 抢一次前台，用来模拟"对局就绪"那种被打回后立刻又顶回来的客户端
        bool fast = !quiet && args.Length > 0 && string.Equals(args[0], "fast", StringComparison.OrdinalIgnoreCase);
        bool modal = !quiet && args.Length > 0 && string.Equals(args[0], "modal", StringComparison.OrdinalIgnoreCase);
        bool stayMin = !quiet && args.Length > 0 && string.Equals(args[0], "minimized", StringComparison.OrdinalIgnoreCase);
        int interval = fast ? 80 : 1200;
        if (quiet) Log("quiet 模式：不自动弹");
        if (fast) Log("fast 模式：每 80ms 抢一次前台");
        if (stayMin) Log("minimized 模式：保持最小化，但持续硬抢前台");
        Application.Run(new FakeClient(quiet, interval, modal, stayMin));
    }
}
