using System;
using System.Collections.Generic;

namespace LolFocusGuard
{
    internal interface IFocusClock { long NowMs { get; } }
    internal interface IFocusWindows
    {
        WindowInfo Foreground { get; }
        WindowInfo Inspect(IntPtr handle);
    }
    internal interface IFocusActions
    {
        void Minimize(IntPtr handle);
        void PostMinimize(IntPtr handle);
        void Restore(IntPtr handle, bool fallback);
        void Wait(int milliseconds);
    }

    internal struct WindowId : IEquatable<WindowId>
    {
        public readonly IntPtr Handle;
        public readonly int Pid;
        public readonly uint Thread;
        public readonly long Birth, Generation;
        public WindowId(IntPtr handle, int pid, uint thread, long birth, long generation)
        { Handle = handle; Pid = pid; Thread = thread; Birth = birth; Generation = generation; }
        public bool Empty { get { return Handle == IntPtr.Zero; } }
        public bool Equals(WindowId other)
        { return Handle == other.Handle && Pid == other.Pid && Thread == other.Thread && Birth == other.Birth && Generation == other.Generation; }
        public override bool Equals(object value) { return value is WindowId && Equals((WindowId)value); }
        public override int GetHashCode() { return Handle.GetHashCode() ^ Pid ^ Birth.GetHashCode() ^ Generation.GetHashCode(); }
        public static bool operator ==(WindowId a, WindowId b) { return a.Equals(b); }
        public static bool operator !=(WindowId a, WindowId b) { return !a.Equals(b); }
    }

    internal sealed class WindowInfo
    {
        public readonly WindowId Id;
        private readonly WindowId[] owners;
        public readonly string Process, Title, Class, Size;
        public readonly bool Target, Shell, Own, Iconic, Login, Client;
        public readonly double ProcessAgeMs;
        public WindowInfo(WindowId id, string process, string title, string cls, bool target,
            bool shell, bool own, bool iconic, bool login, bool client, double age, WindowId[] owners, string size = "?")
        {
            Id = id; Process = process; Title = title; Class = cls; Size = size; Target = target; Shell = shell;
            Own = own; Iconic = iconic; Login = login; Client = client; ProcessAgeMs = age;
            this.owners = owners == null ? new WindowId[0] : (WindowId[])owners.Clone();
        }
        public bool RelatedTo(WindowId clicked)
        {
            if (Id == clicked) return true;
            if (Id.Pid != clicked.Pid || Id.Birth != clicked.Birth) return false;
            foreach (WindowId owner in owners) if (owner == clicked) return true;
            return false;
        }
    }

    internal enum InputSource { Hook, Poll, SystemSwitch, Keyboard }
    internal enum InputEntry { Direct, Shell, Switch }
    internal sealed class InputSnapshot
    {
        public readonly long Sequence, At;
        public readonly int X, Y, Button;
        public readonly WindowInfo Hit;
        public readonly InputSource Source;
        public readonly InputEntry Entry;
        public InputSnapshot(long sequence, long at, int x, int y, int button,
            WindowInfo hit, InputSource source, InputEntry entry)
        { Sequence = sequence; At = at; X = x; Y = y; Button = button; Hit = hit; Source = source; Entry = entry; }
    }

    internal sealed class BlockRequest
    {
        public readonly long Sequence, Transition, Version, Takeover, At;
        public readonly int DelayMs;
        public readonly WindowInfo Target, Restore;
        public readonly bool Minimize;
        internal BlockRequest(long sequence, long transition, long version, long takeover, long at, int delay,
            WindowInfo target, WindowInfo restore, bool minimize)
        { Sequence = sequence; Transition = transition; Version = version; Takeover = takeover; At = at; DelayMs = delay; Target = target; Restore = restore; Minimize = minimize; }
    }

    // Input, foreground observations, pause, retry and action results share one coordinator.
    // The gate protects state only; slow waits never run under it.
    internal sealed class FocusEngine
    {
        public const int LateInputMs = 30, RetryMs = 400, SuppressMs = 15000;
        private readonly object gate = new object();
        private readonly IFocusClock clock;
        private readonly IFocusWindows windows;
        private readonly IFocusActions actions;
        private readonly bool testMode;
        private readonly Action<string> log;
        private readonly Dictionary<WindowId, BlockRequest> queued = new Dictionary<WindowId, BlockRequest>();
        private readonly Dictionary<WindowId, long> minimized = new Dictionary<WindowId, long>();
        private readonly Dictionary<WindowId, long> suppressed = new Dictionary<WindowId, long>();
        private readonly HashSet<string> startup = new HashSet<string>();
        private readonly bool[] buttonDown = new bool[3];
        private readonly long[] buttonAt = new long[] { -1, -1, -1 };
        private readonly long[] buttonSequence = new long[3];
        private readonly long[] hookButtonAt = new long[] { -1, -1, -1 };
        private WindowInfo foreground, accepted;
        private WindowId authorized, lastBlocked, failed;
        private InputSnapshot input;
        private long consumed, inputSequence, requestSequence, transition, version, takeover;
        private long lastAttempt = -1, lastBlock = -1, failedAt = -1, unlockAt = -1;
        private int failures, blockCount;
        private bool paused, locked, stopped, acceptedUser;
        public bool PollingFallback = true;
        public FocusEngine(IFocusClock clock, IFocusWindows windows, IFocusActions actions, bool testMode, Action<string> log)
        { this.clock = clock; this.windows = windows; this.actions = actions; this.testMode = testMode; this.log = log ?? delegate { }; }
        private long Now { get { return clock.NowMs; } }
        internal static double Age(long now, long then)
        { if (then < 0) return double.MaxValue; uint age = unchecked((uint)(now - then)); return age > int.MaxValue ? double.MaxValue : age; }
        public int BlockCount { get { lock (gate) return blockCount; } }
        public bool Paused { get { lock (gate) return paused; } }
        public InputSnapshot LatestInput { get { lock (gate) return input; } }

        public void Initialize(WindowInfo current)
        {
            lock (gate)
            {
                foreground = current;
                if (current != null && !current.Own && !current.Shell) Accept(current, false, true);
            }
        }
        private void Invalidate() { version++; queued.Clear(); lastAttempt = -1; }
        public void Pause(bool value)
        { lock (gate) { paused = value; takeover++; Invalidate(); input = null; consumed = 0; } }
        public void Session(bool isLocked)
        {
            lock (gate)
            {
                locked = isLocked; takeover++; Invalidate(); input = null;
                Array.Clear(buttonDown, 0, buttonDown.Length);
                if (!isLocked) unlockAt = Now;
            }
        }
        public void Stop() { lock (gate) { stopped = true; takeover++; Invalidate(); } }
        public void Destroy(IntPtr handle)
        {
            lock (gate)
            {
                var ids = new List<WindowId>();
                foreach (WindowId id in minimized.Keys) if (id.Handle == handle) ids.Add(id);
                foreach (WindowId id in ids) minimized.Remove(id);
                ids.Clear();
                foreach (WindowId id in suppressed.Keys) if (id.Handle == handle) ids.Add(id);
                foreach (WindowId id in ids) suppressed.Remove(id);
                if (foreground != null && foreground.Id.Handle == handle) { Invalidate(); foreground = null; authorized = new WindowId(); }
                // Keep the accepted identity until the next transition so natural close is detectable.
                if (input != null && input.Hit != null && input.Hit.Id.Handle == handle) input = null;
            }
        }
        public void RememberMinimized(WindowInfo window)
        {
            lock (gate)
            {
                if (window == null || !window.Target || !window.Iconic) return;
                if (minimized.Count >= 512) minimized.Clear();
                minimized[window.Id] = Now;
            }
        }

        public void Button(int button, bool down, WindowInfo hit, int x, int y, InputSource source)
        {
            lock (gate)
            {
                if (stopped || button < 0 || button >= 3) return;
                if (source == InputSource.Poll && Age(Now, hookButtonAt[button]) < 30) return;
                if (source == InputSource.Hook) hookButtonAt[button] = Now;
                if (!down) { buttonDown[button] = false; return; }
                long seq;
                if (buttonDown[button])
                {
                    // A hook can upgrade a poll observation of the same down; it must not
                    // produce a second consumable ticket or extend its original timestamp.
                    if (source != InputSource.Hook || Age(Now, buttonAt[button]) > 50) return;
                    seq = buttonSequence[button];
                }
                else
                {
                    seq = ++inputSequence; buttonDown[button] = true;
                    buttonAt[button] = Now; buttonSequence[button] = seq;
                }
                Publish(new InputSnapshot(seq, buttonAt[button], x, y, button, hit, source,
                    hit != null && hit.Shell ? InputEntry.Shell : InputEntry.Direct));
            }
        }
        public void SwitchGesture(InputSource source)
        {
            lock (gate)
            {
                // Keyboard Alt+Tab and SWITCHSTART describe the same gesture. SWITCHEND
                // must not create a new ticket after the selection has consumed this one.
                if (input != null && input.Entry == InputEntry.Switch && Age(Now, input.At) <= 100 && source == InputSource.SystemSwitch) return;
                Publish(new InputSnapshot(++inputSequence, Now, 0, 0, -1, null, source, InputEntry.Switch));
            }
        }
        public void SwitchEnded()
        {
            lock (gate)
            {
                if (input != null && input.Entry == InputEntry.Switch && input.Sequence > consumed && Age(Now, input.At) <= 10000)
                    Publish(new InputSnapshot(input.Sequence, Now, 0, 0, -1, null, input.Source, InputEntry.Switch));
            }
        }
        private void Publish(InputSnapshot sample)
        {
            if (stopped || (input != null && sample.Sequence < input.Sequence)) return;
            if (input != null && sample.Sequence == input.Sequence && input.Source == InputSource.Hook) return;
            input = sample;
            if (paused || locked || testMode || foreground == null) return;
            DecisionCode code;
            if (sample.Entry == InputEntry.Direct && sample.Hit != null && Matches(foreground, out code))
            {
                Accept(foreground, true);
                log("放行原因: 你点了这个窗口（" + foreground.Process + "，用户接管）");
            }
            else if (sample.Entry != InputEntry.Direct)
            {
                takeover++; lastBlocked = new WindowId(); failures = 0; failedAt = -1;
                Invalidate();
                // Cancel old work while the user is in shell/switch UI. Retry is a fresh
                // decision and can consume this ticket if the target was already activated.
                if (foreground.Target && authorized != foreground.Id) lastAttempt = Now;
            }
        }
        private bool Matches(WindowInfo window, out DecisionCode code)
        {
            code = DecisionCode.BlockNoClick;
            if (testMode || input == null || input.Sequence <= consumed || window == null || window.Shell || window.Own) return false;
            int grace = input.Entry == InputEntry.Direct ? FocusRules.ClickGraceMs : FocusRules.ShellGraceMs;
            if (Age(Now, input.At) > grace) return false;
            if (input.Entry == InputEntry.Direct && input.Hit == null)
            { code = DecisionCode.AllowUncertainInput; return true; }
            if (input.Entry == InputEntry.Direct && !window.RelatedTo(input.Hit.Id))
            { code = DecisionCode.BlockClickElsewhere; return false; }
            code = input.Entry == InputEntry.Direct ? DecisionCode.AllowClickedTarget :
                input.Entry == InputEntry.Shell ? DecisionCode.AllowClickedShell : DecisionCode.AllowSwitching;
            return true;
        }
        private void Accept(WindowInfo window, bool consume, bool keepSession = false)
        {
            if (consume || accepted == null || accepted.Id != window.Id)
            { takeover++; lastBlocked = new WindowId(); failures = 0; failedAt = -1; }
            accepted = window; authorized = window.Id;
            acceptedUser = consume || keepSession || !window.Target;
            if (consume && input != null) consumed = input.Sequence;
            Invalidate();
        }

        public void Foreground(WindowInfo current)
        { lock (gate) { Observe(current, false); } }
        private void Observe(WindowInfo current, bool retry)
        {
            if (stopped || current == null || current.Id.Empty) return;
            bool changed = foreground == null || foreground.Id != current.Id;
            bool ownedByUser = acceptedUser && !authorized.Empty && current.RelatedTo(authorized);
            if (!changed && !retry) return;
            if (changed)
            {
                transition++; Invalidate(); authorized = new WindowId(); foreground = current;
                int grace = input != null && input.Entry == InputEntry.Direct ? FocusRules.ClickGraceMs : FocusRules.ShellGraceMs;
                string evidenceText = input == null || input.Sequence <= consumed || Age(Now, input.At) > grace
                    ? "没按过或已用掉" : Age(Now, input.At) + "ms前@" + (input.Hit == null ? "?" : input.Hit.Process) +
                        " 凭据=" + input.Sequence + " 来源=" + input.Source + " 入口=" + input.Entry;
                log("前台变化 → " + current.Process + " (pid=" + current.Id.Pid + ") class=" + current.Class + " 标题=\"" + current.Title +
                    "\" 尺寸=" + current.Size + " 最小化=" + current.Iconic + " 按键=" + evidenceText);
            }
            if (current.Own || current.Shell || locked) return;
            DecisionCode evidence;
            bool user = Matches(current, out evidence);
            if (!current.Target || paused || (!testMode && accepted != null && current.Id == accepted.Id))
            {
                bool keep = accepted != null && current.Id == accepted.Id && acceptedUser;
                Accept(current, user, keep);
                // The selected app consumes the one-shot ticket; shell intermediate
                // windows do not. This also handles selecting an ordinary browser.
                if (input != null && input.Entry != InputEntry.Direct && Age(Now, input.At) <= FocusRules.ShellGraceMs)
                    consumed = input.Sequence;
                return;
            }
            if (!changed && authorized == current.Id) return;
            bool first = false;
            if (current.Client)
            {
                string key = current.Id.Pid + ":" + current.Id.Birth;
                if (!startup.Contains(key))
                {
                    if (startup.Count >= 512) startup.Clear();
                    startup.Add(key); first = current.ProcessAgeMs < 0 || current.ProcessAgeMs <= 120000;
                }
            }
            long suppression;
            bool suppress = suppressed.TryGetValue(current.Id, out suppression) && Age(Now, suppression) < SuppressMs;
            var facts = new DecisionFacts();
            facts.TestModeOnly = testMode; facts.WeGameLoginPhase = current.Login;
            facts.FirstClientWindow = first; facts.Suppressed = suppress; facts.JustUnlocked = Age(Now, unlockAt) <= 2000;
            facts.UserAuthorized = authorized == current.Id || ownedByUser;
            facts.PrevWindowGone = accepted != null && !SameWindow(accepted);
            facts.InputMatches = user; facts.InputCode = evidence;
            Decision decision = FocusRules.Decide(facts);
            if (decision.Allow)
            {
                if (decision.Code == DecisionCode.AllowSuppressed) { queued.Clear(); lastAttempt = Now; }
                else Accept(current, user, ownedByUser);
                log("放行原因: " + FocusRules.Describe(decision.Code));
                return;
            }
            if (queued.ContainsKey(current.Id)) return;
            log(decision.Code == DecisionCode.BlockClickElsewhere
                ? "不放行: 按键落在其他窗口上（不属于这次目标激活）"
                : "不放行: 没有尚未用掉的鼠标按键或切换凭据");
            long iconic;
            bool minimize = (minimized.TryGetValue(current.Id, out iconic) && Age(Now, iconic) < 5000) ||
                (lastBlocked == current.Id && Age(Now, lastBlock) < 2000);
            WindowInfo restore = accepted != null && SameWindow(accepted) && accepted.Id != current.Id ? accepted : null;
            // A manually minimized client/launcher should stay minimized. Match windows
            // may minimize automatically on focus loss, so still restore those normally.
            if (restore != null)
            {
                WindowInfo liveRestore = windows.Inspect(restore.Id.Handle);
                if (liveRestore == null || liveRestore.Id != restore.Id || (liveRestore.Target && liveRestore.Iconic)) restore = null;
            }
            var request = new BlockRequest(++requestSequence, transition, version, takeover, Now,
                PollingFallback && !testMode ? LateInputMs : 0, current, restore, minimize || restore == null);
            queued[current.Id] = request; lastAttempt = Now;
        }
        private bool SameWindow(WindowInfo expected)
        { WindowInfo live = windows.Inspect(expected.Id.Handle); return live != null && live.Id == expected.Id; }

        public void Tick()
        {
            lock (gate)
            {
                WindowInfo live = windows.Foreground;
                if (live == null) return;
                if (foreground == null || live.Id != foreground.Id) { Observe(live, false); return; }
                if (!live.Target || live.Shell || live.Own || paused || locked || stopped || authorized == live.Id) return;
                if (lastAttempt >= 0 && Age(Now, lastAttempt) >= RetryMs) Observe(live, true);
            }
        }
        public BlockRequest TakeReady()
        {
            lock (gate)
            {
                foreach (BlockRequest request in queued.Values)
                    if (Age(Now, request.At) >= request.DelayMs) return request;
                return null;
            }
        }
        private bool Valid(BlockRequest request)
        {
            if (stopped || paused || locked || request.Version != version || request.Transition != transition) return false;
            BlockRequest latest;
            if (!queued.TryGetValue(request.Target.Id, out latest) || latest.Sequence != request.Sequence) return false;
            if (!SameWindow(request.Target)) return false;
            WindowInfo live = windows.Foreground;
            if (live == null || live.Id != request.Target.Id)
            {
                if (live != null) Observe(live, false);
                return false;
            }
            DecisionCode evidence;
            if (Matches(live, out evidence)) { Accept(live, true); return false; }
            return authorized != live.Id && Age(Now, request.At) <= 2000;
        }
        private bool Step(BlockRequest request, WindowInfo restore, Action action)
        {
            lock (gate)
            {
                if (!Valid(request)) return false;
                if (restore != null)
                {
                    WindowInfo liveRestore = windows.Inspect(restore.Id.Handle);
                    if (liveRestore == null || liveRestore.Id != restore.Id || (liveRestore.Target && liveRestore.Iconic)) return false;
                }
                // The non-waiting OS call and validation share the state lock so a
                // concurrent accepted input cannot leave a validated old action behind.
                action(); return true;
            }
        }
        public void Execute(BlockRequest request)
        {
            if (request == null) return;
            bool acted = false;
            try
            {
                if (request.Minimize)
                {
                    acted = Step(request, null, delegate { actions.Minimize(request.Target.Id.Handle); });
                    if (!acted) return;
                    actions.Wait(5);
                    WindowInfo live = windows.Inspect(request.Target.Id.Handle);
                    if (live != null && live.Id == request.Target.Id && !live.Iconic)
                    {
                        if (!Step(request, null, delegate { actions.PostMinimize(request.Target.Id.Handle); })) return;
                        actions.Wait(15);
                    }
                }
                if (request.Restore != null)
                {
                    bool restored = Step(request, request.Restore, delegate { actions.Restore(request.Restore.Id.Handle, false); });
                    acted |= restored;
                    if (restored)
                    {
                        actions.Wait(10);
                        Step(request, request.Restore, delegate { actions.Restore(request.Restore.Id.Handle, true); });
                    }
                }
            }
            finally { Complete(request, acted); }
        }
        private void Complete(BlockRequest request, bool acted)
        {
            lock (gate)
            {
                bool active = !paused && !locked && !stopped && request.Version == version && request.Transition == transition;
                BlockRequest latest;
                if (queued.TryGetValue(request.Target.Id, out latest) && latest.Sequence == request.Sequence) queued.Remove(request.Target.Id);
                if (acted)
                {
                    blockCount++; lastBlock = Now;
                    if (request.Takeover == takeover) lastBlocked = request.Target.Id;
                    log("拦截 #" + blockCount + "：" + request.Target.Process + " (pid=" + request.Target.Id.Pid +
                        ") 自动抢焦点（class=" + request.Target.Class + "，标题=\"" + request.Target.Title +
                        "\"）→ 打回，request=" + request.Sequence);
                }
                else log("取消拦截（请求 #" + request.Sequence + "）：用户接管、前台改变或窗口身份失效");
                if (!acted) return;
                WindowInfo live = windows.Foreground;
                WindowInfo target = windows.Inspect(request.Target.Id.Handle);
                log("动作结果（请求 #" + request.Sequence + "）：前台 HWND=" + (live == null ? IntPtr.Zero : live.Id.Handle) +
                    "，预期 HWND=" + (request.Restore == null ? IntPtr.Zero : request.Restore.Id.Handle) +
                    "，目标最小化=" + (target != null && target.Id == request.Target.Id && target.Iconic));
                if (request.Restore != null && live != null && live.Id != request.Restore.Id && live.Id != request.Target.Id)
                    log("诊断：打回后前台没有回到预期窗口（预期 " + request.Restore.Process + "，实际 " + live.Process + "）");
                if (request.Takeover != takeover) return;
                if (live == null || live.Id != request.Target.Id)
                { failures = 0; failedAt = -1; if (live != null) Observe(live, false); return; }
                if (!active) return;
                log("打回失败: " + live.Process + " 仍是前台（请求 #" + request.Sequence + "）");
                if (failed != live.Id || Age(Now, failedAt) > 10000) { failed = live.Id; failedAt = Now; failures = 0; }
                if (++failures >= 5)
                {
                    if (suppressed.Count >= 512) suppressed.Clear();
                    suppressed[live.Id] = Now; failures = 0;
                    log("放弃拦截 15 秒：" + live.Process + "，10 秒内连续 5 次未恢复前台");
                }
            }
        }
    }
}
