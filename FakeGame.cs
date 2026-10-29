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

        public GameForm()
        {
            Text = "FakeGame";
            FormBorderStyle = FormBorderStyle.None;      // 无边框全屏 ≈ 全屏游戏
            StartPosition = FormStartPosition.Manual;
            Bounds = Screen.PrimaryScreen.Bounds;
            BackColor = Color.Black;
            _timer = new Timer();
            _timer.Interval = 300;
            _timer.Tick += OnTick;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Log(string.Format("全屏窗口已显示 {0}x{1}", Bounds.Width, Bounds.Height));
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
                    Log(fg ? "拿回前台" : "失去前台");
                }
            }
            catch
            {
            }
        }
    }
}
