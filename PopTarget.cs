using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 测试用小程序：模拟「目标程序自己抢前台」和「用户点它」两种情况
//   PopTarget.exe bg      -> 对照组窗口 BgWin（什么都不做）
//   PopTarget.exe target  -> 被监控窗口 PopTarget（盯 %TEMP%\poptarget.flag）
//       文件内容 pop    -> 自己抢前台（全程无任何输入）
//       文件内容 cycle  -> 先最小化，300ms 后恢复并抢前台（保证一定产生前台切换事件）
internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

    [STAThread]
    private static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch { } // 和守卫一致，坐标一律用物理像素
        bool target = args.Length > 0 && args[0] == "target";

        Form f = new Form();
        f.Text = target ? "PopTarget" : "BgWin";
        f.Width = 420;
        f.Height = 260;
        f.StartPosition = FormStartPosition.Manual;
        // 可选：PopTarget.exe target 600 450 —— 指定窗口左上角（用来把窗口摆到鼠标指针底下）
        int px = target ? 200 : 200;
        int py = target ? 150 : 700;
        if (args.Length >= 3)
        {
            int.TryParse(args[1], out px);
            int.TryParse(args[2], out py);
        }
        f.Location = new Point(px, py);
        f.TopMost = true; // 保证在最上面，模拟点击一定落在它身上

        if (target)
        {
            string flag = Path.Combine(Path.GetTempPath(), "poptarget.flag");
            Timer t = new Timer();
            t.Interval = 200;
            t.Tick += delegate
            {
                if (!File.Exists(flag)) return;
                string mode = "pop";
                try
                {
                    mode = File.ReadAllText(flag).Trim();
                    File.Delete(flag);
                }
                catch { }
                if (mode == "cycle")
                {
                    ShowWindow(f.Handle, 6); // SW_MINIMIZE
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(300);
                }
                ShowWindow(f.Handle, 9); // SW_RESTORE
                for (int i = 0; i < 5; i++)
                {
                    SetForegroundWindow(f.Handle);
                    System.Threading.Thread.Sleep(40);
                }
            };
            t.Start();
        }

        Application.Run(f);
    }
}
