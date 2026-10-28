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

        /// <summary>刚刚才放行过这个窗口（你点了它，客户端内部又抢了一次焦点）。</summary>
        public bool RecentlyAllowed;

        /// <summary>上一次按 Alt/Win 距今多少毫秒。</summary>
        public double ModifierIdleMs = double.MaxValue;

        /// <summary>系统层面最后一次输入距今多少毫秒（GetLastInputInfo）。</summary>
        public int InputAgeMs = int.MaxValue;

        /// <summary>输入那一刻，指针压在谁身上 —— 是监控名单里的进程。</summary>
        public bool InputOnTarget;

        /// <summary>输入那一刻，指针压在任务栏 / 开始菜单 / 桌面上。</summary>
        public bool InputOnShell;

        /// <summary>上一个前台窗口已经不在了（刚关闭/退出）。</summary>
        public bool PrevWindowGone;
    }

    internal enum DecisionCode
    {
        AllowTestMode,        // 理论上不会出现（测试模式下直接拦）
        AllowWeGameLogin,
        AllowFirstClientWindow,
        AllowSuppressed,
        AllowJustAllowed,
        AllowModifier,
        AllowPrevWindowGone,
        AllowClickedTarget,
        AllowClickedShell,
        BlockNoInput,
        BlockInputElsewhere,
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
        public const int InputGraceMs = 500;

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

            // 3.5) 刚刚才放行过这个窗口：你点它 → 它内部又自己抢了一次焦点。
            //      这里压回去的观感就是"我刚点开你就给我最小化"，所以干脆不压。
            if (f.RecentlyAllowed) return new Decision(true, DecisionCode.AllowJustAllowed);

            // 4) 刚按过 Alt / Win（切窗口专用键）
            if (f.ModifierIdleMs <= ModifierGraceMs) return new Decision(true, DecisionCode.AllowModifier);

            // 5) 上一个前台窗口已经没了：前台自然接管，不算它抢
            if (f.PrevWindowGone) return new Decision(true, DecisionCode.AllowPrevWindowGone);

            // 6) 一直没有输入 → 它自己弹的
            if (f.InputAgeMs > InputGraceMs) return new Decision(false, DecisionCode.BlockNoInput);

            // 7) 有输入：输入落在被切到前台的这个窗口上 → 你点的
            if (f.InputOnTarget) return new Decision(true, DecisionCode.AllowClickedTarget);

            // 8) 有输入：输入落在任务栏/开始菜单上 → 你点的
            if (f.InputOnShell) return new Decision(true, DecisionCode.AllowClickedShell);

            // 9) 有输入，但落在别的程序上（你在打游戏/打字） → 它自己弹的
            return new Decision(false, DecisionCode.BlockInputElsewhere);
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

        private static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
