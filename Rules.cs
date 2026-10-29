using System;

namespace LolFocusGuard
{
    // Collected by the coordinator. No timing, input ownership or mutable state lives here.
    internal sealed class DecisionFacts
    {
        public bool TestModeOnly, WeGameLoginPhase, FirstClientWindow, Suppressed;
        public bool JustUnlocked, UserAuthorized, PrevWindowGone, InputMatches;
        public DecisionCode InputCode = DecisionCode.BlockNoClick;
    }
    internal enum DecisionCode
    {
        AllowWeGameLogin, AllowFirstClientWindow, AllowSuppressed, AllowJustUnlocked,
        AllowUserAuthorized, AllowPrevWindowGone, AllowClickedTarget, AllowClickedShell, AllowUncertainInput,
        AllowSwitching, BlockNoClick, BlockClickElsewhere, BlockTestMode
    }
    internal sealed class Decision
    {
        public readonly bool Allow;
        public readonly DecisionCode Code;
        public Decision(bool allow, DecisionCode code) { Allow = allow; Code = code; }
    }
    internal static class FocusRules
    {
        public const int ClickGraceMs = 350, ShellGraceMs = 2500;
        public static string Describe(DecisionCode code)
        {
            switch (code)
            {
                case DecisionCode.AllowWeGameLogin: return "WeGame 登录阶段（客户端还没起来）";
                case DecisionCode.AllowFirstClientWindow: return "客户端窗口第一次出现（登录页/启动页）";
                case DecisionCode.AllowSuppressed: return "正在放弃拦截期（连续压不动）";
                case DecisionCode.AllowJustUnlocked: return "刚解锁/恢复会话";
                case DecisionCode.AllowUserAuthorized: return "用户正在操作（同进程拥有的窗口）";
                case DecisionCode.AllowPrevWindowGone: return "上一个前台窗口已经关闭，前台自动接管";
                case DecisionCode.AllowClickedTarget: return "你点了这个窗口";
                case DecisionCode.AllowClickedShell: return "你点了任务栏/开始菜单";
                case DecisionCode.AllowSwitching: return "Alt+Tab / Win 切换器凭据";
                case DecisionCode.AllowUncertainInput: return "采到按下但落点不确定（只放行当前激活）";
                default: return code.ToString();
            }
        }
        public static bool IsKeyboardSwitch(int key, bool alt, bool win, bool ctrl)
        {
            return (alt && (key == 9 || key == 27)) ||
                (win && (key == 9 || (key >= 0x30 && key <= 0x39) || key == 0x44 || key == 0x4d || (ctrl && (key == 0x25 || key == 0x27)))) ||
                (ctrl && key == 27);
        }
        public static Decision Decide(DecisionFacts f)
        {
            if (f.TestModeOnly) return new Decision(false, DecisionCode.BlockTestMode);
            if (f.InputMatches) return new Decision(true, f.InputCode);
            if (f.UserAuthorized) return new Decision(true, DecisionCode.AllowUserAuthorized);
            if (f.WeGameLoginPhase) return new Decision(true, DecisionCode.AllowWeGameLogin);
            if (f.FirstClientWindow) return new Decision(true, DecisionCode.AllowFirstClientWindow);
            if (f.JustUnlocked) return new Decision(true, DecisionCode.AllowJustUnlocked);
            if (f.PrevWindowGone) return new Decision(true, DecisionCode.AllowPrevWindowGone);
            if (f.Suppressed) return new Decision(true, DecisionCode.AllowSuppressed);
            return new Decision(false, f.InputCode == DecisionCode.BlockClickElsewhere
                ? DecisionCode.BlockClickElsewhere : DecisionCode.BlockNoClick);
        }
    }
    internal static class ProcClass
    {
        // The actual match process is never a blocking target, even in targets.txt.
        public static bool IsGame(string proc) { return Eq(proc, "League of Legends"); }
        public static bool IsTarget(string proc, string title, string cls, bool configured, bool weGameEnabled)
        { return !IsGame(proc) && (configured || (weGameEnabled && IsWeGameWindow(proc, title, cls))); }
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
