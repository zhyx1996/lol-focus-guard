// 测试用的"假游戏"：一个无边框全屏窗口，站在前台不动。
// 用途：验证「客户端被压回去之后，前台有没有回到那个全屏窗口」——
// 真机上就是"打游戏时客户端/WeGame 弹窗"，这是唯一一类守卫自己无法直接观测的观感问题
// （全屏窗口能不能被 SetForegroundWindow 叫回来，跟普通窗口行为不一样）。
//
// 它**故意不抢前台**：只记录"我现在是不是前台"，免得自己把守卫的问题盖住。
using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    // 注意：GetCurrentThreadId 在 kernel32 里，而且**必须写 DllImport** ——
    // 漏了它就是运行时 EntryPointNotFoundException（踩过：线程上一个未处理异常把量具整个崩掉，
    // 日志停在"全屏窗口已显示"，看起来像"卡住"）。
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    // 前后台切换的"量具"：30ms 采样（原来是 300ms —— 一次 100ms 级的抢前台根本采不到），
    // 并且记录"离开前台多久"（= 用户真正被打断的时间）。
    internal static bool ForceForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = (uint)GetWindowThreadProcessId(fg, IntPtr.Zero).ToInt64();
        uint me = GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != 0 && fgThread != me) attached = AttachThreadInput(me, fgThread, true);
            SetForegroundWindow(hwnd);
            if (GetForegroundWindow() != hwnd) SetForegroundWindow(hwnd);
            return GetForegroundWindow() == hwnd;
        }
        finally
        {
            if (attached) AttachThreadInput(me, fgThread, false);
        }
    }

    private static string _logPath;

    internal static void Log(string msg)
    {
        try
        {
            if (_logPath == null)
            {
                _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "faketest.log");
            }
            File.AppendAllText(_logPath, string.Format("[{0:HH:mm:ss.fff}] {1}\r\n", DateTime.Now, msg));
        }
        catch
        {
        }
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Log("======== FakeGame 启动 ========");
        Application.Run(new GameForm());
    }

    private sealed class GameForm : Form
    {
        private readonly Timer _timer;
        private bool _wasFg = true;
        private long _lostAtTicks;

        public GameForm()
        {
            Text = "FakeGame";
            FormBorderStyle = FormBorderStyle.None;      // 无边框全屏 ≈ 全屏游戏
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            BackColor = Color.Black;
            _timer = new Timer();
            _timer.Interval = 30;   // 30ms：要量的是"被打断多久"，300ms 会整段漏掉
            _timer.Tick += OnTick;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Log(string.Format("全屏窗口已显示 {0}x{1}", Bounds.Width, Bounds.Height));
            // ForceForeground 里的 AttachThreadInput 可能挂在目标线程的消息循环上
            // （实测挂过整整一次：日志停在上一行就再无输出），所以放到后台线程 + 1.5 秒超时，
            // 绝不能因为"拿前台"这一下把量具自己弄死。
            bool ok = false;
            System.Threading.Thread grab = new System.Threading.Thread(delegate() { ok = ForceForeground(Handle); });
            grab.IsBackground = true;
            grab.Start();
            if (!grab.Join(1500)) Log("启动拿前台超时（AttachThreadInput 被挂住了）—— 不影响后面的测量，只是起点不确定");
            _wasFg = ok;
            Log(ok ? "启动即前台（可以开始量了）" : "启动没能拿到前台（量不到有效数据，请先手动点一下它）");
            _timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            try
            {
                bool fg = GetForegroundWindow() == Handle;
                if (fg != _wasFg)
                {
                    _wasFg = fg;
                    long now = DateTime.Now.Ticks;
                    if (!fg)
                    {
                        _lostAtTicks = now;
                        Log("失去前台");
                    }
                    else
                    {
                        long ms = _lostAtTicks > 0 ? (now - _lostAtTicks) / TimeSpan.TicksPerMillisecond : -1;
                        Log(ms >= 0 ? string.Format("拿回前台（被打断 {0}ms）", ms) : "拿回前台");
                    }
                }
            }
            catch
            {
            }
        }
    }
}
