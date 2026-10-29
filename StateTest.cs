using System;
using System.Collections.Generic;
using System.Threading;
using LolFocusGuard;

internal sealed class FakeDesktop : IFocusClock, IFocusWindows, IFocusActions
{
    public long Time = 1000;
    public long NowMs { get { return Time; } }
    public readonly Dictionary<IntPtr, WindowInfo> Windows = new Dictionary<IntPtr, WindowInfo>();
    public WindowInfo Current;
    public WindowInfo Foreground { get { return Current; } }
    public readonly List<string> Calls = new List<string>();
    public readonly List<string> Logs = new List<string>();
    public Action OnWait, OnMinimize;
    public bool RejectRestore, RejectMinimize;
    public FocusEngine Engine;
    public WindowInfo Game, Client, WeGame, Browser, Shell;
    public FakeDesktop(bool testMode)
    {
        Game = Add(1, 10, false, false, "Game");
        Client = Add(2, 20, true, false, "LeagueClientUx");
        WeGame = Add(3, 30, true, false, "wegame");
        Browser = Add(4, 40, false, false, "Browser");
        Shell = Add(5, 50, false, true, "explorer");
        Current = Game;
        Engine = new FocusEngine(this, this, this, testMode, Logs.Add);
        Engine.Initialize(Game);
    }
    public WindowInfo Add(int hwnd, int pid, bool target, bool shell, string name)
    { return Put(new WindowInfo(new WindowId((IntPtr)hwnd, pid, (uint)pid, 100, 1), name, name, shell ? "Shell_TrayWnd" : "Window", target, shell, false, false, false, false, 180000, null)); }
    public WindowInfo Put(WindowInfo window) { Windows[window.Id.Handle] = window; return window; }
    public WindowInfo Inspect(IntPtr handle) { WindowInfo window; return Windows.TryGetValue(handle, out window) ? window : null; }
    public WindowInfo Variant(WindowInfo w, bool iconic, bool login, bool client, double age, WindowId[] owners)
    { return Put(new WindowInfo(w.Id, w.Process, w.Title, w.Class, w.Target, w.Shell, w.Own, iconic, login, client, age, owners)); }
    public void Go(WindowInfo window) { Current = window; Engine.Foreground(window); }
    public void Click(WindowInfo hit, InputSource source)
    { Engine.Button(0, true, hit, 50, 50, source); Engine.Button(0, false, null, 50, 50, source); }
    public BlockRequest Ready() { Time += 31; Engine.Tick(); return Engine.TakeReady(); }
    public void Run() { Engine.Execute(Ready()); }
    public void Wait(int ms) { Time += ms; Action action = OnWait; OnWait = null; if (action != null) action(); }
    public void Minimize(IntPtr handle)
    {
        Calls.Add("minimize:" + handle);
        if (!RejectMinimize) { WindowInfo w = Inspect(handle); Put(Variant(w, true, w.Login, w.Client, w.ProcessAgeMs, null)); }
        if (OnMinimize != null) OnMinimize();
    }
    public void PostMinimize(IntPtr handle) { Calls.Add("post:" + handle); }
    public void Restore(IntPtr handle, bool fallback)
    { Calls.Add((fallback ? "fallback:" : "restore:") + handle); if (!RejectRestore) Current = Inspect(handle); }
    public int Count(string prefix) { return Calls.FindAll(delegate(string s) { return s.StartsWith(prefix); }).Count; }
}

internal static class StateTest
{
    private static int passed, failed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Case(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("ok   " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
    }
    private static FakeDesktop Desk() { return new FakeDesktop(false); }
    private static void NoActions(FakeDesktop d) { Check(d.Calls.Count == 0 && d.Engine.BlockCount == 0, "unexpected window action"); }
    private static void Main()
    {
        Case("idle target restores exact previous game", delegate {
            var d = Desk(); d.Go(d.Client); d.Run(); Check(d.Current.Id == d.Game.Id && d.Count("restore:") == 1, "game not restored");
        });
        Case("action result logs actual and expected foreground identities", delegate {
            var d = Desk(); d.Go(d.Client); d.Run();
            Check(d.Logs.Exists(delegate(string line) { return line.Contains("动作结果") && line.Contains("前台 HWND=1，预期 HWND=1"); }), "restore outcome missing");
        });
        Case("failed focus restoration leaves explicit failure evidence", delegate {
            var d = Desk(); d.RejectRestore = true; d.Go(d.Client); d.Run();
            Check(d.Logs.Exists(delegate(string line) { return line.StartsWith("打回失败:"); }), "failure not logged");
        });
        Case("changed foreground during action logs observed destination without chasing it", delegate {
            var d = Desk(); d.Engine.RememberMinimized(d.Variant(d.Client, true, false, false, 0, null));
            d.OnMinimize = delegate { d.Current = d.Browser; }; d.Go(d.Client); d.Run();
            Check(d.Current.Id == d.Browser.Id && d.Count("restore:") == 0 && d.Logs.Exists(delegate(string line) { return line.StartsWith("诊断：打回后前台没有回到预期窗口"); }), "unexpected foreground chased or not recorded");
        });
        Case("direct click permits manual client activation", delegate {
            var d = Desk(); d.Click(d.Client, InputSource.Hook); d.Go(d.Client); d.Run(); NoActions(d);
        });
        Case("restore destination minimized after enqueue is not reopened", delegate {
            var d = Desk(); d.Click(d.WeGame, InputSource.Hook); d.Go(d.WeGame); d.Go(d.Client);
            BlockRequest request = d.Ready(); d.Variant(d.WeGame, true, false, false, 0, null);
            d.Engine.Execute(request); NoActions(d);
        });
        Case("user can continue operating client beyond three seconds", delegate {
            var d = Desk(); d.Click(d.Client, InputSource.Hook); d.Go(d.Client); d.Time += 20000; d.Engine.Tick(); d.Run(); NoActions(d);
        });
        Case("leaving client revokes authorization immediately", delegate {
            var d = Desk(); d.Click(d.Client, InputSource.Hook); d.Go(d.Client); d.Go(d.Game); d.Go(d.Client); d.Run(); Check(d.Current.Id == d.Game.Id, "old authorization leaked");
        });
        Case("clicking WeGame does not authorize a different target", delegate {
            var d = Desk(); d.Click(d.WeGame, InputSource.Hook); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "unrelated target authorized");
        });
        Case("clicking another window of same process does not authorize target", delegate {
            var d = Desk(); var other = d.Add(6, 20, true, false, "LeagueClientUx"); d.Click(other, InputSource.Hook); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "same PID was enough");
        });
        Case("clicked owner permits its same-process dialog", delegate {
            var d = Desk(); var dialog = d.Add(6, 20, true, false, "LeagueClientUx"); dialog = d.Variant(dialog, false, false, false, 0, new WindowId[] { d.Client.Id }); d.Click(d.Client, InputSource.Hook); d.Go(dialog); d.Run(); NoActions(d);
        });
        Case("user-owned dialog may open after input ticket expired", delegate {
            var d = Desk(); d.Click(d.Client, InputSource.Hook); d.Go(d.Client); d.Time += 10000;
            var dialog = d.Add(6, 20, true, false, "LeagueClientUx"); dialog = d.Variant(dialog, false, false, false, 0, new WindowId[] { d.Client.Id });
            d.Go(dialog); d.Run(); NoActions(d);
        });
        Case("startup exemption does not authorize later owned popup", delegate {
            var d = Desk(); d.Client = d.Variant(d.Client, false, false, true, 1000, null); d.Go(d.Client); d.Time += 1000;
            var dialog = d.Add(6, 20, true, false, "LeagueClientUx"); dialog = d.Variant(dialog, false, false, true, 2000, new WindowId[] { d.Client.Id });
            d.Go(dialog); d.Run(); Check(d.Engine.BlockCount == 1, "startup became user authorization");
        });
        Case("foreign-process owner relationship does not authorize dialog", delegate {
            var d = Desk(); var dialog = d.Add(6, 60, true, false, "Other"); dialog = d.Variant(dialog, false, false, false, 0, new WindowId[] { d.Client.Id }); d.Click(d.Client, InputSource.Hook); d.Go(dialog); d.Run(); Check(d.Engine.BlockCount == 1, "foreign dialog authorized");
        });
        Case("taskbar slow activation at 2400ms is allowed", delegate {
            var d = Desk(); d.Click(d.Shell, InputSource.Hook); d.Time += 2400; d.Go(d.Client); d.Run(); NoActions(d);
        });
        foreach (string cls in new string[] { "TaskListThumbnailWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame" })
        {
            string entry = cls;
            Case("shell entry survives intermediate window: " + entry, delegate {
                var d = Desk(); var shell = d.Put(new WindowInfo(d.Shell.Id, "explorer", "", entry, false, true, false, false, false, false, 0, null));
                d.Click(shell, InputSource.Hook); d.Go(shell); d.Go(d.Client); d.Run(); NoActions(d);
            });
        }
        Case("taskbar-to-browser ticket cannot authorize later client popup", delegate {
            var d = Desk(); d.Click(d.Shell, InputSource.Hook); d.Go(d.Shell); d.Go(d.Browser); d.Go(d.Client); d.Run(); Check(d.Current.Id == d.Browser.Id && d.Engine.BlockCount == 1, "ticket reused");
        });
        Case("expired shell ticket is rejected", delegate {
            var d = Desk(); d.Click(d.Shell, InputSource.Hook); d.Time += 2501; d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "expired ticket accepted");
        });
        Case("direct click after 351ms is rejected", delegate {
            var d = Desk(); d.Click(d.Client, InputSource.Hook); d.Time += 351; d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "expired direct click accepted");
        });
        Case("direct click at 350ms is allowed", delegate {
            var d = Desk(); d.Click(d.Client, InputSource.Hook); d.Time += 350; d.Go(d.Client); d.Run(); NoActions(d);
        });
        Case("unknown input hit allows only current activation", delegate {
            var d = Desk(); d.Click(null, InputSource.Poll); d.Go(d.Client); d.Run(); NoActions(d);
            d.Go(d.Game); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "uncertain input became persistent exemption");
        });
        Case("shell ticket boundary at 2500ms is allowed", delegate {
            var d = Desk(); d.Click(d.Shell, InputSource.Hook); d.Time += 2500; d.Go(d.Client); d.Run(); NoActions(d);
        });
        Case("Alt+Tab switch start allows activation", delegate {
            var d = Desk(); d.Engine.SwitchGesture(InputSource.SystemSwitch); d.Go(d.Client); d.Engine.SwitchEnded(); d.Run(); NoActions(d);
        });
        Case("long Alt+Tab selection supports delayed activation after switch end", delegate {
            var d = Desk(); d.Engine.SwitchGesture(InputSource.SystemSwitch); d.Time += 6000; d.Engine.SwitchEnded(); d.Time += 2400; d.Go(d.Client); d.Run(); NoActions(d);
        });
        Case("switch end cannot recreate consumed Alt+Tab ticket", delegate {
            var d = Desk(); d.Engine.SwitchGesture(InputSource.Keyboard); d.Engine.SwitchGesture(InputSource.SystemSwitch); d.Go(d.Browser); d.Engine.SwitchEnded(); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "switch ticket reused");
        });
        Case("Win+number creates one-use keyboard switch ticket", delegate {
            var d = Desk(); d.Engine.SwitchGesture(InputSource.Keyboard); d.Go(d.WeGame); d.Go(d.Game); d.Go(d.WeGame); d.Run(); Check(d.Engine.BlockCount == 1, "keyboard authorization persisted");
        });
        Case("game clicks do not grant client activation", delegate {
            var d = Desk(); d.Click(d.Game, InputSource.Hook); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1 && d.Current.Id == d.Game.Id, "game click allowed client");
        });
        Case("game click arriving during late-input wait does not cancel block", delegate {
            var d = Desk(); d.Go(d.Client); d.Time += 15; d.Click(d.Game, InputSource.Poll); d.Run(); Check(d.Engine.BlockCount == 1, "unrelated late click cancelled request");
        });
        Case("late polling click cancels pending request and all retries", delegate {
            var d = Desk(); d.Go(d.Client); d.Time += 15; d.Click(d.Client, InputSource.Poll); d.Run(); d.Time += 1000; d.Engine.Tick(); d.Run(); NoActions(d);
        });
        Case("polling request is not executable before 30ms", delegate {
            var d = Desk(); d.Go(d.Client); d.Time += 29; Check(d.Engine.TakeReady() == null, "late wait shortened"); d.Time++; Check(d.Engine.TakeReady() != null, "late wait exceeded bound");
        });
        Case("hook and poll held button yield one snapshot", delegate {
            var d = Desk(); d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Hook); long seq = d.Engine.LatestInput.Sequence;
            d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Poll); d.Go(d.Client); d.Time += 100; d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Poll);
            Check(d.Engine.LatestInput.Sequence == seq, "duplicated physical down"); d.Go(d.Game); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "duplicate revived consumed ticket");
        });
        Case("hook upgrades polling snapshot without extending its time", delegate {
            var d = Desk(); d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Poll); var before = d.Engine.LatestInput; d.Time += 10; d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Hook);
            var after = d.Engine.LatestInput; Check(before.Sequence == after.Sequence && before.At == after.At && after.Source == InputSource.Hook, "upgrade minted new ticket");
        });
        Case("poll state before hook delivery cannot split one press", delegate {
            var d = Desk(); d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Hook); var first = d.Engine.LatestInput;
            d.Engine.Button(0, false, null, 1, 2, InputSource.Poll); d.Time += 15; d.Engine.Button(0, true, d.Client, 1, 2, InputSource.Poll);
            Check(first.Sequence == d.Engine.LatestInput.Sequence, "premature poll release minted new press");
        });
        Case("queued action cancelled by direct user takeover", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Click(d.Client, InputSource.Hook); d.Engine.Execute(request); NoActions(d);
        });
        Case("queued action cancelled by pause even after resume", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Engine.Pause(true); d.Engine.Pause(false); d.Engine.Execute(request); NoActions(d);
        });
        Case("queued action cancelled on session lock", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Engine.Session(true); d.Engine.Execute(request); NoActions(d);
        });
        Case("unlock grace is bounded and cannot renew user evidence", delegate {
            var d = Desk(); d.Engine.Session(true); d.Engine.Session(false); d.Go(d.Client); d.Run(); NoActions(d);
            d.Go(d.Game); d.Time += 2001; d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "unlock exemption persisted");
        });
        Case("queued action cancelled on exit", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Engine.Stop(); d.Engine.Execute(request); NoActions(d);
        });
        Case("unobserved foreground change cancels stale action", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Current = d.Browser; d.Engine.Execute(request); NoActions(d);
        });
        Case("HWND PID reuse invalidates target", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Current = d.Add(2, 200, true, false, "Reused"); d.Engine.Execute(request); NoActions(d);
        });
        Case("same PID window generation reuse invalidates target", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Engine.Destroy(d.Client.Id.Handle); var w = d.Client;
            d.Current = d.Put(new WindowInfo(new WindowId(w.Id.Handle, w.Id.Pid, w.Id.Thread, w.Id.Birth, 2), w.Process, "", "", true, false, false, false, false, false, 0, null));
            d.Engine.Execute(request); NoActions(d);
        });
        Case("process lifetime reuse invalidates target even with same PID", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); var w = d.Client;
            d.Current = d.Put(new WindowInfo(new WindowId(w.Id.Handle, w.Id.Pid, w.Id.Thread, 200, w.Id.Generation), w.Process, "", "", true, false, false, false, false, false, 0, null));
            d.Engine.Execute(request); NoActions(d);
        });
        Case("expired queued action does not execute", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Time += 2100; d.Engine.Execute(request); NoActions(d);
        });
        Case("restore destination reuse never receives focus", delegate {
            var d = Desk(); d.Go(d.Client); var request = d.Ready(); d.Add(1, 999, false, false, "Other"); d.Engine.Execute(request); NoActions(d);
        });
        Case("post-minimize rechecks user takeover during wait", delegate {
            var d = Desk(); d.RejectMinimize = true; d.Client = d.Variant(d.Client, true, false, false, 0, null); d.Engine.RememberMinimized(d.Client); d.Go(d.Client);
            d.OnWait = delegate { d.Click(d.Client, InputSource.Hook); }; d.Run(); Check(d.Count("minimize:") == 1 && d.Count("post:") == 0 && d.Count("restore:") == 0, "old action continued after takeover");
        });
        Case("input on another thread can cancel action during wait", delegate {
            var d = Desk(); d.RejectMinimize = true; d.Client = d.Variant(d.Client, true, false, false, 0, null); d.Engine.RememberMinimized(d.Client); d.Go(d.Client);
            d.OnWait = delegate {
                var thread = new Thread(delegate() { d.Click(d.Client, InputSource.Hook); });
                thread.IsBackground = true; thread.Start(); Check(thread.Join(1000), "input blocked behind action wait");
            };
            d.Run(); Check(d.Count("post:") == 0 && d.Count("restore:") == 0, "old action survived parallel input");
        });
        Case("system choosing another foreground after minimize is respected", delegate {
            var d = Desk(); d.Client = d.Variant(d.Client, true, false, false, 0, null); d.Engine.RememberMinimized(d.Client); d.Go(d.Client);
            d.OnMinimize = delegate { d.Current = d.Browser; }; d.Run();
            Check(d.Count("restore:") == 0 && d.Current.Id == d.Browser.Id, "stole focus from a different foreground");
        });
        Case("fallback restoration rechecks pause during wait", delegate {
            var d = Desk(); d.RejectRestore = true; d.Go(d.Client); d.OnWait = delegate { d.Engine.Pause(true); }; d.Run(); Check(d.Count("restore:") == 1 && d.Count("fallback:") == 0, "fallback ignored pause");
        });
        Case("restore target may be manually accepted WeGame", delegate {
            var d = Desk(); d.Click(d.WeGame, InputSource.Hook); d.Go(d.WeGame); d.Go(d.Client); d.Run(); Check(d.Current.Id == d.WeGame.Id, "monitored restore target excluded");
        });
        Case("manually minimized WeGame is not reopened as restore destination", delegate {
            var d = Desk(); d.Click(d.WeGame, InputSource.Hook); d.Go(d.WeGame);
            d.Variant(d.WeGame, true, false, false, 0, null); d.Go(d.Client); d.Run();
            Check(d.Count("restore:") == 0 && d.Count("minimize:") == 1, "reopened minimized launcher");
        });
        Case("game exiting allows natural client handoff", delegate {
            var d = Desk(); d.Windows.Remove(d.Game.Id.Handle); d.Engine.Destroy(d.Game.Id.Handle); d.Go(d.Client); d.Run(); NoActions(d);
        });
        Case("first startup is allowed once per process lifetime", delegate {
            var d = Desk(); d.Client = d.Variant(d.Client, false, false, true, 1000, null); d.Go(d.Client); d.Run(); NoActions(d);
            d.Go(d.Game); var second = d.Add(6, 20, true, false, "LeagueClientUx"); second = d.Variant(second, false, false, true, 1500, null); d.Go(second); d.Run(); Check(d.Engine.BlockCount == 1, "new HWND repeated startup exemption");
        });
        Case("WeGame login compatibility does not authorize unrelated client", delegate {
            var d = Desk(); d.WeGame = d.Variant(d.WeGame, false, true, false, 0, null); d.Go(d.WeGame); d.Run(); NoActions(d); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "login became user ticket");
        });
        Case("suppression follows actual failed actions and later expires", delegate {
            var d = Desk(); d.RejectRestore = true; d.RejectMinimize = true; d.Go(d.Client);
            for (int i = 0; i < 5; i++) { d.Run(); d.Time += 401; d.Engine.Tick(); }
            int before = d.Engine.BlockCount; d.Run(); Check(d.Engine.BlockCount == before, "failed action suppression absent");
            d.Time += 15001; d.Engine.Tick(); d.Run(); Check(d.Engine.BlockCount > before, "suppression never expired");
        });
        Case("successful repeated interception never triggers suppression", delegate {
            var d = Desk();
            for (int i = 0; i < 12; i++) { d.Go(d.Client); d.Run(); Check(d.Current.Id == d.Game.Id, "foreground not restored"); }
            Check(d.Engine.BlockCount == 12, "successful blocks suppressed");
        });
        Case("rapid repeated popup escalates to minimize after focus-only restore", delegate {
            var d = Desk(); d.Go(d.Client); d.Run(); Check(d.Count("minimize:") == 0, "first popup was unnecessarily minimized");
            d.Go(d.Client); d.Run(); Check(d.Count("minimize:") == 1, "repeated popup was not escalated");
        });
        Case("different target supersedes old pending foreground request", delegate {
            var d = Desk(); d.Go(d.Client); var old = d.Ready(); d.Go(d.WeGame); var current = d.Ready();
            d.Engine.Execute(old); NoActions(d); d.Engine.Execute(current); Check(d.Current.Id == d.Game.Id, "restored wrong target");
        });
        Case("new transition replaces previously dequeued request", delegate {
            var d = Desk(); d.Go(d.Client); var old = d.Ready(); d.Go(d.Game); d.Go(d.Client); var current = d.Ready(); d.Engine.Execute(old); NoActions(d); d.Engine.Execute(current); Check(d.Engine.BlockCount == 1, "new request lost");
        });
        Case("test-only no-click-allow still blocks manual target activation", delegate {
            var d = new FakeDesktop(true); d.Click(d.Client, InputSource.Hook); d.Go(d.Client); d.Run(); Check(d.Engine.BlockCount == 1, "test mode ignored");
        });
        Case("tick count wrap preserves direct-click expiry", delegate {
            var d = Desk(); d.Time = uint.MaxValue - 10L; d.Click(d.Client, InputSource.Hook); d.Time = 20; d.Go(d.Client); d.Run(); NoActions(d);
        });
        Console.WriteLine("State tests: " + passed + " passed, " + failed + " failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
}
