using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LolFocusGuard;

// Opt-in interactive integration suite. Never kills a running guard or touches its log.
internal static class DesktopTest
{
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int X, Y; public uint Data, Flags, Time; public IntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input
    { public uint Type; public InputUnion Data; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr GetProp(IntPtr hwnd, string name);
    [DllImport("user32.dll")] private static extern bool SetProp(IntPtr hwnd, string name, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    private const uint RaiseMessage = 0x8002;
    private static int pass, fail, skip;
    private static void Result(string name, bool value)
    { Console.WriteLine((value ? "PASS " : "FAIL ") + name); if (value) pass++; else fail++; }
    private static void Pump(int ms)
    {
        var time = Stopwatch.StartNew();
        while (time.ElapsedMilliseconds < ms) { Application.DoEvents(); Thread.Sleep(5); }
    }
    private static void Raise(IntPtr hwnd)
    {
        uint pid; uint foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
        uint targetThread = Native.GetWindowThreadProcessId(hwnd, out pid);
        uint current = Native.GetCurrentThreadId(); bool a = false, b = false;
        try
        {
            if (foregroundThread != 0 && foregroundThread != current) a = Native.AttachThreadInput(current, foregroundThread, true);
            if (targetThread != 0 && targetThread != current && targetThread != foregroundThread) b = Native.AttachThreadInput(current, targetThread, true);
            Native.ShowWindowAsync(hwnd, 9); Native.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (b) Native.AttachThreadInput(current, targetThread, false);
            if (a) Native.AttachThreadInput(current, foregroundThread, false);
        }
    }
    private sealed class Fixture : Form
    {
        private int clicks, activations;
        public Fixture(bool target)
        {
            Text = target ? "Focus guard test target" : "Focus guard test background";
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(target ? 500 : 50, 120, 350, 250);
            Activated += delegate { SetProp(Handle, "FocusGuardTest.Activations", (IntPtr)(++activations)); };
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x201) SetProp(Handle, "FocusGuardTest.Clicks", (IntPtr)(++clicks));
            if (message.Msg == RaiseMessage) { Raise(Handle); return; }
            base.WndProc(ref message);
        }
    }
    private static IntPtr Window(Process process)
    {
        for (int i = 0; i < 100; i++)
        {
            process.Refresh(); if (process.HasExited) break;
            if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            Pump(30);
        }
        throw new Exception("Fixture did not create a window");
    }
    private static bool Click(IntPtr hwnd)
    {
        Native.RECT rect; Native.GetWindowRect(hwnd, out rect);
        SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, 0, 0, 0x13); // topmost, do not activate
        int before = GetProp(hwnd, "FocusGuardTest.Clicks").ToInt32();
        if (!SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)) return false;
        Pump(80);
        var down = new Input(); down.Data.Mouse.Flags = 2;
        var up = new Input(); up.Data.Mouse.Flags = 4;
        uint sent = SendInput(1, new Input[] { down }, Marshal.SizeOf(typeof(Input)));
        Pump(50);
        sent += SendInput(1, new Input[] { up }, Marshal.SizeOf(typeof(Input)));
        Pump(100);
        return sent == 2 && GetProp(hwnd, "FocusGuardTest.Clicks").ToInt32() > before;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        if (args.Length > 0 && args[0] == "--fixture")
        { Application.Run(new Fixture(args.Length > 1 && args[1] == "target")); return 0; }
        if (args.Length > 0 && args[0] == "--smoke")
        {
            string smokeLog = Path.Combine(Path.GetTempPath(), "focus-native-smoke-" + Guid.NewGuid().ToString("N") + ".log");
            using (var smoke = new Guard(new string[] { "__FocusGuard_NoSuchProcess_Smoke" }, smokeLog, true, false, null))
            {
                IntPtr hwnd = Native.GetForegroundWindow();
                WindowInfo first = smoke.Inspect(hwnd); Pump(150); WindowInfo second = smoke.Inspect(hwnd);
                Result("native identity and process mapping are stable", first != null && second != null && first.Id == second.Id && first.Id.Pid > 0 && first.Process.Length > 0);
                Result("read-only native smoke performs zero block actions", smoke.BlockCount == 0);
                smoke.Pause(TimeSpan.FromMinutes(1)); Result("native pause", smoke.Paused);
                smoke.Resume(); Result("native resume", !smoke.Paused);
            }
            Console.WriteLine("Native smoke log: " + smokeLog);
            return fail == 0 ? 0 : 1;
        }
        if (args.Length == 0 || args[0] != "--interactive")
        { Console.WriteLine("SKIP: pass --interactive to run desktop integration tests"); return 2; }
        foreach (string name in new string[] { "LolFocusGuard", "FocusFixture", "FocusBackdrop", "League of Legends" })
        {
            Process[] active = Process.GetProcessesByName(name);
            bool busy = active.Length > 0; foreach (Process process in active) process.Dispose();
            if (busy) { Console.WriteLine("SKIP: " + name + " is running; leave the current session untouched"); return 2; }
        }
        string directory = Path.Combine(Path.GetTempPath(), "focus-guard-desktop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "guard.log");
        string assembly = typeof(DesktopTest).Assembly.Location;
        Process background = null, target = null; Guard guard = null;
        IntPtr original = Native.GetForegroundWindow(); Native.POINT cursor; Native.GetCursorPos(out cursor);
        IntPtr bg = IntPtr.Zero, pt = IntPtr.Zero;
        try
        {
            string bgFile = Path.Combine(directory, "FocusBackdrop.exe"), ptFile = Path.Combine(directory, "FocusFixture.exe");
            File.Copy(assembly, bgFile); File.Copy(assembly, ptFile);
            background = Process.Start(new ProcessStartInfo(bgFile, "--fixture bg") { UseShellExecute = false, CreateNoWindow = true });
            target = Process.Start(new ProcessStartInfo(ptFile, "--fixture target") { UseShellExecute = false, CreateNoWindow = true });
            bg = Window(background); pt = Window(target);
            Raise(bg); Pump(300);
            if (Native.GetForegroundWindow() != bg) { Console.WriteLine("SKIP: Windows refused the foreground prerequisite"); return 2; }
            guard = new Guard(new string[] { "FocusFixture" }, log, true, false, null);
            Pump(300);
            int before = guard.BlockCount;
            if (Click(pt))
            {
                Pump(1000);
                Result("manual click keeps exact target foreground, visible, and zero block actions", Native.GetForegroundWindow() == pt && !Native.IsIconic(pt) && guard.BlockCount == before);
                bool backgroundClick = Click(bg); Pump(100);
                Result("user can leave target immediately", backgroundClick && Native.GetForegroundWindow() == bg);
            }
            else { Console.WriteLine("SKIP: synthetic click did not reach fixture; manual activation unverified"); skip++; Raise(bg); Pump(300); }
            if (Native.GetForegroundWindow() != bg) throw new Exception("background not foreground before automatic-popup phase");
            // No three-second exemption: switching away must revoke authorization now.
            for (int i = 0; i < 3; i++)
            {
                Native.ShowWindowAsync(pt, 6); Pump(250);
                int activated = GetProp(pt, "FocusGuardTest.Activations").ToInt32(); before = guard.BlockCount;
                Native.PostMessage(pt, RaiseMessage, IntPtr.Zero, IntPtr.Zero); Pump(600);
                bool raised = GetProp(pt, "FocusGuardTest.Activations").ToInt32() > activated;
                if (!raised) { Console.WriteLine("SKIP: popup did not become foreground; cycle " + i); skip++; continue; }
                Result("automatic popup restores exact background HWND and target minimized, cycle " + i,
                    Native.GetForegroundWindow() == bg && Native.IsIconic(pt) && guard.BlockCount > before);
            }
            Pump(300);
            string text = File.Exists(log) ? File.ReadAllText(log) : "";
            Result("complete log contains actual actions", text.Contains("拦截 #") && text.Contains("前台变化 → FocusFixture"));
        }
        catch (Exception ex) { Result(ex.ToString(), false); }
        finally
        {
            if (guard != null) guard.Dispose();
            IntPtr current = Native.GetForegroundWindow();
            foreach (Process process in new Process[] { target, background })
            {
                if (process == null) continue;
                try { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(1000)) process.Kill(); } }
                finally { process.Dispose(); }
            }
            if ((current == pt || current == bg) && Native.IsWindow(original)) Raise(original);
            SetCursorPos(cursor.X, cursor.Y);
            Console.WriteLine("Logs retained: " + log);
        }
        Console.WriteLine("Desktop tests: " + pass + " passed, " + fail + " failed, " + skip + " skipped");
        return fail > 0 ? 1 : skip > 0 ? 2 : 0;
    }
}
