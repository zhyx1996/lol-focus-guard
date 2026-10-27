using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);

    [STAThread]
    private static void Main(string[] args)
    {
        bool target = args.Length > 0 && args[0] == "target";

        Form f = new Form();
        f.Text = target ? "PopTarget" : "BgWin";
        f.Width = 420;
        f.Height = 260;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = target ? new Point(1100, 120) : new Point(1100, 520);
        f.TopMost = false;

        if (target)
        {
            string flag = Path.Combine(Path.GetTempPath(), "poptarget.flag");
            Timer t = new Timer();
            t.Interval = 300;
            t.Tick += delegate
            {
                if (!File.Exists(flag)) return;
                try { File.Delete(flag); } catch { }
                ShowWindow(f.Handle, 9);
                SetForegroundWindow(f.Handle);
            };
            t.Start();
        }

        Application.Run(f);
    }
}
