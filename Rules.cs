// 判定规则的纯函数版本：不碰任何窗口、不读任何全局状态，只吃"事实"，吐"放行/拦截 + 原因"。
// 这样规则表可以被穷举测试（见 LogicTest.cs），改规则时不用真开客户端去试。
using System;

namespace LolFocusGuard
{
    /// <summary>判定用到的全部事实。由 Guard 收集，测试里直接构造。</summary>
    internal sealed class DecisionFacts
    {
        /// <summary>--no-click-allow：测试模式，任何输入都不放行。</summary>
        public bool TestModeOnly;

        /// <summary>被切到前台的窗口是 WeGame，而且客户端进程还没起来（国服登录阶段）。</summary>
        public bool WeGameLoginPhase;

        /// <summary>被切到前台的窗口是客户端进程的窗口，且是第一次见到。</summary>
        public bool FirstClientWindow;

        /// <summary>这个窗口正在"放弃拦截"期里（防拉锯）。</summary>
        public bool Suppressed;

        /// <summary>
        /// 刚刚解锁 / 恢复了会话（2 秒内）。锁屏时客户端在前台，解锁后 Windows 会把前台
        /// 还给它 —— 那一跳是系统行为，不是你让它弹的；拿它判自弹就会"我刚解锁它就被缩掉"。
        /// </summary>
        public bool JustUnlocked;

        /// <summary>
        /// 刚刚放行过**这个窗口**（3 秒内）：你点了它，它内部又抢了一次焦点。
        /// 这条是「我刚点开你就给我最小化」的防线，不能动。
        /// </summary>
        public bool SameWindowJustAllowed;

        /// <summary>
        /// 刚刚放行过**同一个程序的另一个窗口**（600ms 内，且那个窗口之前就见过）。
        /// 只给很短的窗口：再宽就等于"你在客户端里点过任何地方 → 它 3 秒内自弹什么都放行"，
        /// 「匹配成功」弹窗就是这么漏过去的。客户端新造的窗口不适用这条。
        /// </summary>
        public bool SameProcJustAllowed;

        /// <summary>上一次按 Alt/Win 距今多少毫秒。</summary>
        public double ModifierIdleMs = double.MaxValue;

        /// <summary>
        /// Alt+Tab 切换器此刻还开着（EVENT_SYSTEM_SWITCHSTART 到了、SWITCHEND 还没到）。
        /// 慢客户端从 Alt+Tab 到你松手之后才画出来，光靠"最近按过 Alt"（1.5 秒）不够。
        /// </summary>
        public bool Switching;

        /// <summary>Alt+Tab 切换器关掉（SWITCHEND）距今多少毫秒。没关过就是 MaxValue。</summary>
        public double SwitchEndedMs = double.MaxValue;

        /// <summary>
        /// 任务切换器（Alt+Tab 浮层 / ForegroundStaging）刚出现多久 —— **一次性凭据**，
        /// 只用一次就作废（Guard 那边放行后立刻清掉）。
        ///
        /// 为什么还要这条：这台 Win11 上 EVENT_SYSTEM_SWITCHSTART/END 一次都不发，
        /// 只能按窗口身份认切换器。但"看见切换器就开 2.5 秒无防窗口"代价太大
        /// （你 Alt+Tab 到任何程序，那 2.5 秒里客户端自弹都会被放行）。
        /// 所以改成：切换器出现 = 给你**下一次**窗口激活一张一次性凭据，用完即失效。
        /// </summary>
        public double SwitcherPendingMs = double.MaxValue;

        /// <summary>最近一次「鼠标物理按键按下」距今多少毫秒。没按过就是 MaxValue。</summary>
        public double ClickAgeMs = double.MaxValue;

        /// <summary>按下的那一刻，指针压在谁身上 —— 是监控名单里的进程（=你点了它）。</summary>
        public bool ClickOnTarget;

        /// <summary>按下的那一刻，指针压在任务栏 / 开始菜单 / 桌面上（=你点了任务栏按钮）。</summary>
        public bool ClickOnShell;

        /// <summary>上一个前台窗口已经不在了（刚关闭/退出）。</summary>
        public bool PrevWindowGone;
    }

    internal enum DecisionCode
    {
        AllowWeGameLogin,
        AllowFirstClientWindow,
        AllowSuppressed,
        AllowJustUnlocked,
        AllowJustAllowed,
        AllowJustAllowedProc,
        AllowModifier,
        AllowSwitching,
        AllowSwitchCredential,
        AllowPrevWindowGone,
        AllowClickedTarget,
        AllowClickedShell,
        BlockNoClick,
        BlockClickElsewhere,
        BlockTestMode
    }

    internal sealed class Decision
    {
        public bool Allow;
        public DecisionCode Code;

        public Decision(bool allow, DecisionCode code)
        {
            Allow = allow;
            Code = code;
        }
    }

    internal static class FocusRules
    {
        public const int ModifierGraceMs = 1500;
        public const int ClickGraceMs = 350;   // 按键按下后多久内算"这次切换是你点的"
        public const int SwitchEndGraceMs = 2500; // Alt+Tab 切换器关掉后多久内还算"你切的"

        /// <summary>一次性切换凭据的有效期（切换器出现后这么久内落到前台的那个窗口算你切的）。</summary>
        public const int SwitchCredentialMs = 2500;

        /// <summary>
        /// 判定顺序就是优先级，改顺序 = 改行为，测试里有对应用例。
        /// </summary>
        public static Decision Decide(DecisionFacts f)
        {
            // 0) --no-click-allow：测试模式，什么都不放行
            if (f.TestModeOnly) return new Decision(false, DecisionCode.BlockTestMode);

            // 1) 国服登录阶段：客户端还没起来，WeGame 的窗口全放行
            if (f.WeGameLoginPhase) return new Decision(true, DecisionCode.AllowWeGameLogin);

            // 2) 客户端窗口第一次出现（登录页 / 启动页 / 安全检测）
            if (f.FirstClientWindow) return new Decision(true, DecisionCode.AllowFirstClientWindow);

            // 3) 正在防拉锯的放弃期里
            if (f.Suppressed) return new Decision(true, DecisionCode.AllowSuppressed);

            // 3.4) 刚解锁 / 恢复会话（2 秒内）：锁屏时客户端就在前台，解锁后 Windows 会把
            //      前台还给它。这一跳是**系统恢复**，不是你让它弹的 —— 拿它判自弹就会
            //      "我刚解锁它就被缩掉"。放行理由足够硬（会话事件），所以排在"刚放行过"之前；
            //      但排在 WeGame 登录阶段 / 首次窗口之后（那两条是更强的"这次一定是正常的"）。
            if (f.JustUnlocked) return new Decision(true, DecisionCode.AllowJustUnlocked);

            // 3.5) 刚刚才放行过**这个窗口**：你点它 → 它内部又自己抢了一次焦点。
            //      这里压回去的观感就是"我刚点开你就给我最小化"，所以干脆不压。
            if (f.SameWindowJustAllowed) return new Decision(true, DecisionCode.AllowJustAllowed);

            // 3.6) 刚刚放行过**同一个程序的另一个窗口**：只给 600ms（Guard 那边还会要求
            //      "那个 hwnd 之前就见过"）。原来这里是"同进程 3 秒"，等于你在客户端里点过
            //      聊天框/开始匹配之后，它 3 秒内自弹什么都放行 —— 「匹配成功」弹窗就是这么漏的。
            if (f.SameProcJustAllowed) return new Decision(true, DecisionCode.AllowJustAllowedProc);

            // 4) 刚按过 Alt / Win（切窗口专用键）
            if (f.ModifierIdleMs <= ModifierGraceMs) return new Decision(true, DecisionCode.AllowModifier);

            // 4.5) Alt+Tab 兜底：切换器还开着，或者刚松开（SWITCHEND 之后 2.5 秒内）。
            //      慢客户端的问题是"你 Alt+Tab 过去，它 2 秒后才把窗口画出来"，
            //      那时 1.5 秒的 Alt 宽限早就过了，会被判成自弹 → 误拦。
            //      切换器开着/刚关掉是比"按过 Alt"更硬的证据，所以单独给一条，不改 ModifierGraceMs。
            if (f.Switching || f.SwitchEndedMs <= SwitchEndGraceMs)
                return new Decision(true, DecisionCode.AllowSwitching);

            // 4.6) 一次性切换凭据：切换器出现过（这台机器上 SWITCH 事件不发，只能按窗口身份认），
            //      那么**下一次**前台激活就是你切过去的；用过一次就作废（Guard 里清掉），
            //      不会像"2.5 秒宽限"那样留一整段无防窗口。
            if (f.SwitcherPendingMs <= SwitchCredentialMs)
                return new Decision(true, DecisionCode.AllowSwitchCredential);

            // 5) 上一个前台窗口已经没了：前台自然接管，不算它抢
            if (f.PrevWindowGone) return new Decision(true, DecisionCode.AllowPrevWindowGone);

            // 6) 最近这段时间里根本没有「按键按下」→ 它自己弹的
            //    （只看"有没有输入"是不够的：鼠标随手一动也算输入，会把自发弹窗误判成你点的）
            if (f.ClickAgeMs > ClickGraceMs) return new Decision(false, DecisionCode.BlockNoClick);

            // 7) 按键按下的那一刻指针就在被切到前台的窗口上 → 你点的
            if (f.ClickOnTarget) return new Decision(true, DecisionCode.AllowClickedTarget);

            // 8) 按在任务栏/开始菜单上（点任务栏按钮、缩略图、通知）→ 你点的
            if (f.ClickOnShell) return new Decision(true, DecisionCode.AllowClickedShell);

            // 9) 按在别的程序上（你在打游戏/打字/点别处） → 它自己弹的
            return new Decision(false, DecisionCode.BlockClickElsewhere);
        }

        /// <summary>
        /// 「这一下是你在切窗口」的判据：点击落点 + Win 键，两条通道二选一。
        ///
        /// 用在两处，所以抽成纯函数（判错一次就是漏拦或误拦，值得有穷举用例）：
        ///   1) 排队的最小化动作**动手前复核**（Guard.ShouldStillBlock）：
        ///      入队到执行之间这几十毫秒里你的切换手势到了 → 放弃这次打回；
        ///   2) 「窗口赖在前台」的**重试**判断（Guard 的 userTakingOver）：同一套判据决定别再重试。
        ///
        /// **为什么必须认落点**：游戏里右键走位（每秒好几次）、点桌面、点聊天框，
        /// 都只是"按下了鼠标"，不是"你要切窗口"。不认落点的话，这些点击只要恰好落进
        /// "入队 → 执行"那几毫秒，就会把一次真正的拦截取消掉 —— 真机表现就是「偶发漏拦」。
        ///
        /// **为什么只认 Win 不认 Alt**：LoL 里 Alt+Q/W/E/R 是自施法，Alt 按得极频繁。
        /// 真正的 Alt+Tab 不需要这条兜：切换器事件（SWITCHSTART/SWITCHEND + 2.5 秒宽限）
        /// 和 ModifierGraceMs（1.5 秒）早就让那一轮根本走不到"拦"。
        /// </summary>
        /// <param name="shellEntry">落点是 shell 的"点击入口"（任务栏/缩略图/托盘溢出/开始菜单/任务视图）</param>
        /// <param name="targetWindow">落点是被监控进程的窗口（你直接点了客户端）</param>
        /// <param name="winPressed">刚按下了 Win（Win+数字 / Win+方向 / Win+D…）</param>
        public static bool IsSwitchGesture(bool shellEntry, bool targetWindow, bool winPressed)
        {
            if (winPressed) return true;   // Win+数字：明确的"切到第 N 个窗口"
            if (shellEntry) return true;   // 任务栏/缩略图/开始菜单：明确的"点它把它叫出来"
            return targetWindow;           // 直接点了被监控的窗口
        }
    }

    /// <summary>
    /// 进程 / 窗口的分类判据。全部是纯字符串比较 —— 不碰窗口、不读全局状态，
    /// 所以能像规则表一样穷举测试（LogicTest.cs 里有一节专门验它）。
    ///
    /// 为什么要单独拎出来：真机上 WeGame 的界面并不是 wegame.exe 画的，它跑在
    /// browser.exe（class CefTopWindow、标题 "WeGame"）和 pallas.exe 里；
    /// 而 browser.exe 这名字又被 QQ 用着（class QBCoreAx）。按进程名一刀切会两头出错：
    /// 该拦的 WeGame 窗口漏掉，不该拦的 QQ 窗口被当成 WeGame。
    /// </summary>
    internal static class ProcClass
    {
        public static bool IsShell(string proc)
        {
            return Eq(proc, "explorer")
                || Eq(proc, "StartMenuExperienceHost")
                || Eq(proc, "ShellExperienceHost")
                || Eq(proc, "SearchHost");
        }

        public static bool IsClient(string proc)
        {
            return Eq(proc, "LeagueClientUx")
                || Eq(proc, "LeagueClient")
                || Eq(proc, "RiotClientServices");
        }

        public static bool IsWeGame(string proc)
        {
            return Eq(proc, "wegame") || Eq(proc, "wegame_env");
        }

        /// <summary>
        /// 这个窗口算不算 WeGame 的。三种证据，命中任意一条即可：
        ///   1) 进程就是 wegame.exe / wegame_env.exe；
        ///   2) 进程是 pallas.exe（WeGame 的内嵌网页宿主，窗口类 TWINCONTROL）；
        ///   3) 进程是 browser.exe 且窗口标题含 "WeGame"（CEF 那条腿）；
        ///      或者类名是 CefTopWindow 且标题就是 "WeGame…"（标题足够独特，认标题也够）。
        /// QQ 那个 browser.exe（class QBCoreAx、标题为空）不会被认成 WeGame。
        /// </summary>
        public static bool IsWeGameWindow(string proc, string title, string cls)
        {
            if (IsWeGame(proc)) return true;
            if (Eq(proc, "pallas")) return true;
            bool titleIsWeGame = title != null && title.IndexOf("WeGame", StringComparison.OrdinalIgnoreCase) >= 0;
            if (Eq(proc, "browser") && titleIsWeGame) return true;
            if (titleIsWeGame && Eq(cls, "CefTopWindow")) return true;
            return false;
        }

        /// <summary>
        /// 这个前台窗口是不是「任务切换器」本身（Alt+Tab 那个浮层、任务视图、以及切换过程中
        /// 那个 0x0 的 ForegroundStaging）。
        ///
        /// 为什么要按窗口身份认：实测这台 Win11 上 `EVENT_SYSTEM_SWITCHSTART/SWITCHEND`
        /// **一次都不发**（心跳里「切换器事件=0次」，而日志里明明有"任务切换"窗口出现）。
        /// 于是"Alt+Tab 是用户的切换动作"这条证据完全没被采到，用户按 Alt+Tab 把客户端切出来
        /// 会被当成"它自己弹的"打回去 —— 正是用户报的"想切出来切不出来"。
        /// 所以看到这几个窗口就把 SWITCHEND 的宽限期打上（每次看见都续，最多 2.5 秒后自然过期）。
        /// </summary>
        public static bool IsSwitcherWindow(string proc, string title, string cls)
        {
            if (!IsShell(proc)) return false;
            if (Eq(cls, "ForegroundStaging")) return true;
            if (Eq(cls, "MultitaskingViewFrame")) return true;
            if (Eq(cls, "TaskSwitcherWnd")) return true;
            if (Eq(cls, "XamlExplorerHostIslandWindow"))
            {
                // 这个类也用于"任务视图/虚拟桌面"，标题是"任务切换"时就是 Alt+Tab 浮层；
                // 其它标题（空/任务视图）一样算切换动作，用户确实在做窗口切换。
                return true;
            }
            return false;
        }

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
