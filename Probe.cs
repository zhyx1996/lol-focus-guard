// 实验工具：验证「能不能直接让它抢不到前台」，而不是事后回滚。
//   Probe.exe raw                     —— 注册 Raw Input（RIDEV_INPUTSINK），10 秒内数收到多少 WM_INPUT
//   Probe.exe raise <hwnd>            —— 用 AttachThreadInput 把窗口抬到前台（模拟有权限的程序抢焦点）
//   Probe.exe noactivate <hwnd> on|off—— 给窗口加/去 WS_EX_NOACTIVATE
//   Probe.exe lock / unlock           —— LockSetForegroundWindow(LSFW_LOCK / LSFW_UNLOCK)
//   Probe.exe fg                      —— 打印当前前台进程名
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class Probe
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const int RIM_TYPEMOUSE = 0;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool LockSetForegroundWindow(uint code);
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr lParam, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    private static string ProcName(IntPtr h)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    private static IntPtr ParseHwnd(string s)
    {
        long v;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            long.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out v);
        else
            long.TryParse(s, out v);
        return (IntPtr)v;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (mode == "fg")
        {
            IntPtr fg = GetForegroundWindow();
            Console.WriteLine(ProcName(fg) + " hwnd=" + fg + " 最小化=" + IsIconic(fg));
            return 0;
        }

        if (mode == "raise" && args.Length > 1)
        {
            IntPtr h = ParseHwnd(args[1]);
            uint pid;
            uint target = GetWindowThreadProcessId(h, out pid);
            uint foreground = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            uint cur = GetCurrentThreadId();
            bool a1 = false, a2 = false;
            try
            {
                if (target != 0 && target != cur) a1 = AttachThreadInput(cur, target, true);
                if (foreground != 0 && foreground != cur) a2 = AttachThreadInput(cur, foreground, true);
                ShowWindow(h, 9);
                bool ok = SetForegroundWindow(h);
                System.Threading.Thread.Sleep(150);
                Console.WriteLine("SetForegroundWindow=" + ok + " 当前前台=" + ProcName(GetForegroundWindow()));
            }
            finally
            {
                if (a2) AttachThreadInput(cur, foreground, false);
                if (a1) AttachThreadInput(cur, target, false);
            }
            return 0;
        }

        if (mode == "noactivate" && args.Length > 2)
        {
            IntPtr h = ParseHwnd(args[1]);
            bool on = args[2].Equals("on", StringComparison.OrdinalIgnoreCase);
            long v = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            long nv = on ? (v | WS_EX_NOACTIVATE) : (v & ~(long)WS_EX_NOACTIVATE);
            SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)nv);
            Console.WriteLine(string.Format("style 0x{0:X} → 0x{1:X}（NOACTIVATE={2}）", v, nv, (nv & WS_EX_NOACTIVATE) != 0));
            return 0;
        }

        if (mode == "lock" || mode == "unlock")
        {
            bool ok = LockSetForegroundWindow(mode == "lock" ? 1u : 2u);
            Console.WriteLine("LockSetForegroundWindow(" + (mode == "lock" ? "LOCK" : "UNLOCK") + ")=" + ok);
            return 0;
        }

        if (mode == "raw")
        {
            Application.EnableVisualStyles();
            using (RawForm f = new RawForm())
            {
                f.Show();
                Console.WriteLine("RegisterRawInputDevices=" + f.Registered + " handle=" + f.Handle);
                Stopwatch sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 10000)
                {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(5);
                }
                Console.WriteLine(string.Format("10 秒内 RAW INPUT: 鼠标移动 {0}、按键按下 {1}、其他设备 {2}",
                    f.Moves, f.Buttons, f.Others));
                if (!f.Registered) Console.WriteLine("（注册失败 —— Raw Input 这条路在本机不可用）");
            }
            return 0;
        }

        Console.WriteLine("用法: raw | raise <hwnd> | noactivate <hwnd> on|off | lock | unlock | fg");
        return 1;
    }

    private sealed class RawForm : Form
    {
        public int Moves;
        public int Buttons;
        public int Others;
        public bool Registered;

        public RawForm()
        {
            ShowInTaskbar = false;
            WindowState = FormWindowState.Minimized;
            FormBorderStyle = FormBorderStyle.None;
            Width = 1;
            Height = 1;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RAWINPUTDEVICE[] devs = new RAWINPUTDEVICE[1];
            devs[0].UsagePage = 1;
            devs[0].Usage = 2;
            devs[0].Flags = RIDEV_INPUTSINK;
            devs[0].Target = Handle;
            Registered = RegisterRawInputDevices(devs, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT)
            {
                uint size = 0;
                GetRawInputData(m.LParam, RID_INPUT, IntPtr.Zero, ref size, 24);
                if (size > 0)
                {
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(m.LParam, RID_INPUT, buf, ref size, 24) == size)
                        {
                            int type = Marshal.ReadInt32(buf);
                            if (type == RIM_TYPEMOUSE)
                            {
                                Moves++;
                                short flags = Marshal.ReadInt16(buf, 24);
                                if ((flags & 0x0001) != 0 || (flags & 0x0002) != 0) Buttons++;
                            }
                            else
                            {
                                Others++;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
            }
            base.WndProc(ref m);
        }
    }
}
