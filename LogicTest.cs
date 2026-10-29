// 规则表的穷举测试：不起任何窗口、不注入任何输入，直接喂"事实"给 FocusRules.Decide，
// 检查每条规则在每种组合下给出的是放行还是拦截。改规则前后跑一遍，防止把"你手动切窗口"改成"拦"。
//
// 核心事实：判"是不是你点的"不看"最近有没有输入"（鼠标随手一动就算输入，太粗），
// 而是看「鼠标物理按键有没有真的按下」+「按下那一刻指针落在谁身上」。
using System;
using LolFocusGuard;

internal static class LogicTest
{
    private static int _pass;
    private static int _fail;

    private static void Case(string name, DecisionFacts f, bool expectAllow, DecisionCode expectCode)
    {
        Decision d = FocusRules.Decide(f);
        bool ok = d.Allow == expectAllow && d.Code == expectCode;
        if (ok)
        {
            _pass++;
            Console.WriteLine("  ok   " + name);
        }
        else
        {
            _fail++;
            Console.WriteLine(string.Format("  FAIL {0}：期望 {1}/{2}，实际 {3}/{4}",
                name, expectAllow ? "放行" : "拦截", expectCode, d.Allow ? "放行" : "拦截", d.Code));
        }
    }

    private static void Class(string name, string proc, string title, string cls, bool expect)
    {
        bool actual = ProcClass.IsWeGameWindow(proc, title, cls);
        Report(name, actual == expect, expect ? "算 WeGame" : "不算 WeGame", actual ? "算 WeGame" : "不算 WeGame");
    }

    private static void Client(string name, string proc, bool expect)
    {
        bool actual = ProcClass.IsClient(proc);
        Report(name, actual == expect, expect ? "是客户端" : "不是客户端", actual ? "是客户端" : "不是客户端");
    }

    private static void Report(string name, bool ok, string expect, string actual)
    {
        if (ok) { _pass++; Console.WriteLine("  ok   " + name); }
        else { _fail++; Console.WriteLine(string.Format("  FAIL {0}：期望 {1}，实际 {2}", name, expect, actual)); }
    }

    /// <summary>基线：客户端自己跳前台、没有任何按键按下。</summary>
    private static DecisionFacts Idle()
    {
        DecisionFacts f = new DecisionFacts();
        f.ClickAgeMs = 9000;      // 很久没按鼠标
        f.ModifierIdleMs = 60000; // 很久没按 Alt/Win
        return f;
    }

    private static void Main()
    {
        Console.WriteLine("=== 规则表 ===");

        // ---- 拦截：它自己弹的（没有任何按键按下） ----
        Case("没有任何按键按下，客户端自己跳前台 → 拦截", Idle(), false, DecisionCode.BlockNoClick);

        DecisionFacts f = Idle();
        f.ClickAgeMs = 200;       // 你刚按了鼠标，但按在别处
        f.ClickOnTarget = false;
        f.ClickOnShell = false;
        Case("按键按在别的程序上（你在打游戏/点别处）→ 拦截", f, false, DecisionCode.BlockClickElsewhere);

        f = Idle();
        f.ClickAgeMs = 351;       // 刚好超过 350ms 宽限
        Case("按键按下已经过了 351ms → 拦截（宽限窗口外）", f, false, DecisionCode.BlockNoClick);

        // ---- 放行：你自己切的 ----
        f = Idle();
        f.ClickAgeMs = 300;
        f.ClickOnTarget = true;
        Case("按键按下且指针在客户端窗口上 → 放行（你点的）", f, true, DecisionCode.AllowClickedTarget);

        f = Idle();
        f.ClickAgeMs = 300;
        f.ClickOnShell = true;
        Case("按键按下且指针在任务栏上 → 放行（你点的任务栏按钮）", f, true, DecisionCode.AllowClickedShell);

        f = Idle();
        f.ClickAgeMs = 350;       // 边界：正好 350ms 还算数
        f.ClickOnTarget = true;
        Case("按键按下正好 350ms 前 → 放行（边界）", f, true, DecisionCode.AllowClickedTarget);

        f = Idle();
        f.ModifierIdleMs = 300;   // 刚按过 Alt
        Case("刚按过 Alt/Win → 放行（哪怕没按鼠标）", f, true, DecisionCode.AllowModifier);

        f = Idle();
        f.ModifierIdleMs = 1500;
        Case("Alt/Win 正好 1500ms 前 → 放行（边界）", f, true, DecisionCode.AllowModifier);

        f = Idle();
        f.ModifierIdleMs = 1501;
        Case("Alt/Win 过了 1501ms → 不算数", f, false, DecisionCode.BlockNoClick);

        f = Idle();
        f.PrevWindowGone = true;
        Case("上一个前台窗口已关闭（刚打完这局）→ 放行", f, true, DecisionCode.AllowPrevWindowGone);

        // ---- 放行：登录 / 启动阶段 ----
        f = Idle();
        f.WeGameLoginPhase = true;
        Case("客户端没起来时的 WeGame 窗口 → 放行（登录面板）", f, true, DecisionCode.AllowWeGameLogin);

        f = Idle();
        f.FirstClientWindow = true;
        Case("客户端窗口第一次出现 → 放行（登录页/启动页）", f, true, DecisionCode.AllowFirstClientWindow);

        f = Idle();
        f.WeGameLoginPhase = true;
        f.ClickAgeMs = 100;
        f.ClickOnTarget = true;
        Case("WeGame 登录放行优先于点击判据 → 仍放行", f, true, DecisionCode.AllowWeGameLogin);

        // ---- 放行：防拉锯 ----
        f = Idle();
        f.Suppressed = true;
        Case("正在防拉锯放弃期 → 放行（不再跟它拉锯）", f, true, DecisionCode.AllowSuppressed);

        // ---- 放行：刚放行过的窗口（同一个 hwnd，3 秒） ----
        // 真机上踩到的：你点了客户端（放行），客户端内部又自己抢了一次焦点，
        // 3 秒内再压回去的观感就是"我刚点开你就给我最小化"。
        f = Idle();
        f.SameWindowJustAllowed = true;
        Case("同一个窗口刚放行过（3 秒内）→ 放行，不再压回去", f, true, DecisionCode.AllowJustAllowed);

        f = Idle();
        f.SameWindowJustAllowed = true;
        f.ClickAgeMs = 100;
        f.ClickOnTarget = false;
        Case("同窗口刚放行过（3 秒内）优先于「按在别处」→ 仍放行", f, true, DecisionCode.AllowJustAllowed);

        f = Idle();
        f.SameWindowJustAllowed = true;
        f.TestModeOnly = true;
        Case("测试模式优先于「刚放行过」→ 还是拦", f, false, DecisionCode.BlockTestMode);

        // ---- 放行：同进程**另一个窗口**只给 600ms，且客户端新造的窗口一点都不给 ----
        f = Idle();
        f.SameProcJustAllowed = true;
        Case("同进程、之前见过的另一个窗口，刚放行过（600ms 内）→ 放行", f, true, DecisionCode.AllowJustAllowedProc);

        f = Idle();
        f.SameProcJustAllowed = false;  // Guard 侧：900ms 已经超过 600ms，这个 fact 不会被置上
        f.ClickAgeMs = 900;
        Case("客户端在跑，你 900ms 前点过它，现在它自弹 → 拦（同进程宽限只有 600ms）", f, false, DecisionCode.BlockNoClick);

        f = Idle();
        f.SameProcJustAllowed = false;  // Guard 侧：新 hwnd 的 knownBefore=false，永远不会置这个 fact
        f.ClickAgeMs = 200;
        Case("同进程但是新造的窗口（之前没见过，不享受同进程宽限）→ 不因宽限放行", f, false, DecisionCode.BlockClickElsewhere);

        // ---- 边界：同一个 hwnd 的 3 秒防线不能被拆档改坏 ----
        f = Idle();
        f.SameWindowJustAllowed = true;
        Case("同一个窗口刚放行过（2.9 秒，防线边界内）→ 仍放行", f, true, DecisionCode.AllowJustAllowed);

        f = Idle();
        f.SameWindowJustAllowed = false; // Guard 侧：3010ms >= 3000ms，这个 fact 不会被置上
        f.ClickAgeMs = 9000;
        Case("同一个窗口但已过 3 秒（3010ms）→ 不再放行，没按键就拦", f, false, DecisionCode.BlockNoClick);

        // ---- 测试模式 ----
        f = Idle();
        f.TestModeOnly = true;
        f.ClickAgeMs = 50;
        f.ClickOnTarget = true;
        Case("--no-click-allow 测试模式 → 哪怕你真点了也拦", f, false, DecisionCode.BlockTestMode);

        // ---- 组合：真实场景拼一起 ----
        f = Idle();
        f.WeGameLoginPhase = false;   // 客户端已经在跑
        f.ClickAgeMs = 8000;          // 你正在看别的窗口
        Case("客户端在跑，WeGame 卡片自弹、你没在操作 → 拦截", f, false, DecisionCode.BlockNoClick);

        f = Idle();
        f.ClickAgeMs = 250;
        f.ClickOnShell = true;
        Case("客户端在跑，你点了任务栏想切回客户端 → 放行", f, true, DecisionCode.AllowClickedShell);

        f = Idle();
        f.ClickAgeMs = 250;
        f.ClickOnTarget = true;
        f.FirstClientWindow = false;
        Case("客户端在跑，你自己点客户端窗口 → 放行", f, true, DecisionCode.AllowClickedTarget);

        // ---- 窗口分类：WeGame 的界面跑在 browser/pallas 里，而 browser.exe 又被 QQ 用着 ----
        Class("wegame.exe 的窗口 → WeGame", "wegame", "WeGame", "TWINCONTROL", true);
        Class("wegame_env.exe 的窗口 → WeGame", "wegame_env", "", "TWINCONTROL", true);
        Class("browser.exe 标题 WeGame（CEF 那条腿）→ WeGame", "browser", "WeGame", "CefTopWindow", true);
        Class("browser.exe 标题 WeGame 商店 → WeGame", "browser", "WeGame 商城", "CefTopWindow", true);
        Class("pallas.exe（内嵌网页宿主）→ WeGame", "pallas", "SIDE_WEB_VIEW", "TWINCONTROL", true);
        Class("QQ 的 browser.exe（QBCoreAx、无标题）→ 不是 WeGame", "browser", "", "QBCoreAx", false);
        Class("客户端窗口 → 不是 WeGame", "LeagueClientUx", "League of Legends", "RCLIENT", false);
        Class("登录程序窗口 → 不是 WeGame", "Client", "英雄联盟登录程序", "TWINCONTROL", false);
        Class("浏览器窗口 → 不是 WeGame", "msedge", "WeGame 官网 - Microsoft Edge", "Chrome_WidgetWin_1", false);
        Class("任务栏 → 是 shell", "explorer", "", "Shell_TrayWnd", false);
        Client("LeagueClientUx → 客户端", "LeagueClientUx", true);
        Client("RiotClientServices → 客户端", "RiotClientServices", true);
        Client("wegame → 不是客户端", "wegame", false);

        Console.WriteLine();
        Console.WriteLine(string.Format("结果: {0}，通过 {1}，失败 {2}", _fail == 0 ? "PASS" : "FAIL", _pass, _fail));
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }
}
