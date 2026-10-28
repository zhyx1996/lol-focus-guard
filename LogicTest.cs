// 规则表的穷举测试：不起任何窗口、不注入任何输入，直接喂"事实"给 FocusRules.Decide，
// 检查每条规则在每种组合下给出的是放行还是拦截。改规则前后跑一遍，防止把"你手动切窗口"改成"拦"。
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

    /// <summary>基线：客户端自己跳前台、没有任何输入。</summary>
    private static DecisionFacts Idle()
    {
        DecisionFacts f = new DecisionFacts();
        f.InputAgeMs = 9000;
        f.ModifierIdleMs = 60000;
        return f;
    }

    private static void Main()
    {
        Console.WriteLine("=== 规则表 ===");

        // ---- 拦截：它自己弹的 ----
        Case("没有任何输入，客户端自己跳前台 → 拦截", Idle(), false, DecisionCode.BlockNoInput);

        DecisionFacts f = Idle();
        f.InputAgeMs = 200;      // 500ms 内有输入
        f.InputOnTarget = false;
        f.InputOnShell = false;
        Case("有输入但落在别的程序上（你在打游戏）→ 拦截", f, false, DecisionCode.BlockInputElsewhere);

        f = Idle();
        f.InputAgeMs = 501;      // 刚好超过宽限
        Case("输入已经过了 501ms → 拦截（宽限窗口外）", f, false, DecisionCode.BlockNoInput);

        // ---- 放行：你自己切的 ----
        f = Idle();
        f.InputAgeMs = 300;
        f.InputOnTarget = true;
        Case("有输入且点在客户端窗口上 → 放行", f, true, DecisionCode.AllowClickedTarget);

        f = Idle();
        f.InputAgeMs = 300;
        f.InputOnShell = true;
        Case("有输入且点在任务栏上 → 放行", f, true, DecisionCode.AllowClickedShell);

        f = Idle();
        f.InputAgeMs = 9000;
        f.ModifierIdleMs = 300;  // 刚按过 Alt
        Case("刚按过 Alt/Win → 放行（哪怕没有别的输入）", f, true, DecisionCode.AllowModifier);

        f = Idle();
        f.ModifierIdleMs = 1500;
        Case("Alt/Win 正好 1500ms 前 → 放行（边界）", f, true, DecisionCode.AllowModifier);

        f = Idle();
        f.ModifierIdleMs = 1501;
        Case("Alt/Win 过了 1501ms → 不算数", f, false, DecisionCode.BlockNoInput);

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
        f.InputAgeMs = 100;
        f.InputOnTarget = true;
        Case("WeGame 登录放行优先于输入判据 → 仍放行", f, true, DecisionCode.AllowWeGameLogin);

        // ---- 放行：防拉锯 ----
        f = Idle();
        f.Suppressed = true;
        Case("正在防拉锯放弃期 → 放行（不再跟它拉锯）", f, true, DecisionCode.AllowSuppressed);

        // ---- 放行：刚放行过的窗口 ----
        // 真机上踩到的：你点了客户端（放行），客户端内部又自己抢了一次焦点，
        // 800ms 内再压回去的观感就是"我刚点开你就给我最小化"。
        f = Idle();
        f.RecentlyAllowed = true;
        Case("刚放行过的窗口又自己抢前台（800ms 内）→ 放行，不再压回去", f, true, DecisionCode.AllowJustAllowed);

        f = Idle();
        f.RecentlyAllowed = true;
        f.InputAgeMs = 100;
        f.InputOnTarget = false;
        Case("刚放行过（800ms 内）优先于「输入落在别处」→ 仍放行", f, true, DecisionCode.AllowJustAllowed);

        f = Idle();
        f.RecentlyAllowed = true;
        f.Suppressed = false;
        f.TestModeOnly = true;
        Case("测试模式优先于「刚放行过」→ 还是拦", f, false, DecisionCode.BlockTestMode);

        // ---- 测试模式 ----
        f = Idle();
        f.TestModeOnly = true;
        f.InputAgeMs = 50;
        f.InputOnTarget = true;
        Case("--no-click-allow 测试模式 → 哪怕你真点了也拦", f, false, DecisionCode.BlockTestMode);

        // ---- 组合：真实场景拼一起 ----
        f = Idle();
        f.WeGameLoginPhase = false;   // 客户端已经在跑
        f.InputAgeMs = 8000;          // 你正在看别的窗口
        Case("客户端在跑，WeGame 卡片自弹、你没在操作 → 拦截", f, false, DecisionCode.BlockNoInput);

        f = Idle();
        f.InputAgeMs = 250;
        f.InputOnShell = true;
        Case("客户端在跑，你点了任务栏想切回客户端 → 放行", f, true, DecisionCode.AllowClickedShell);

        f = Idle();
        f.InputAgeMs = 250;
        f.InputOnTarget = true;
        f.FirstClientWindow = false;
        Case("客户端在跑，你自己点客户端窗口 → 放行", f, true, DecisionCode.AllowClickedTarget);

        Console.WriteLine();
        Console.WriteLine(string.Format("结果: {0}，通过 {1}，失败 {2}", _fail == 0 ? "PASS" : "FAIL", _pass, _fail));
        Environment.ExitCode = _fail == 0 ? 0 : 1;
    }
}
