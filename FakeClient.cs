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

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    private static string _logPath;

    private sealed class FakeClient : Form
    {
        private readonly Timer _timer;
        private readonly bool _quiet;
        private bool _minimizedOnce;

        /// <summary>
        /// quiet = 不自动弹（窗口就静静待着）。给"需要确定前台顺序"的测试用：
        /// 自动弹的 FakeClient 每秒抢一次前台，Alt+Tab 的 MRU 顺序根本没法控制。
        /// </summary>
        public FakeClient(bool quiet)
        {
            _quiet = quiet;
            Text = "FakeClient";
            ClientSize = new Size(420, 260);
            StartPosition = FormStartPosition.Manual;
            Location = new Point(200, 150);

            _timer = new Timer();
            _timer.Interval = 1200;
            _timer.Tick += OnTick;
            if (_quiet) _timer.Enabled = false;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_quiet) _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
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
        if (quiet) Log("quiet 模式：不自动弹");
        Application.Run(new FakeClient(quiet));
    }
}
