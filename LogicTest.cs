using System;
using LolFocusGuard;

internal static class LogicTest
{
    private static int passed, failed;
    private static void Check(string name, bool ok)
    {
        if (ok) { passed++; Console.WriteLine("ok   " + name); }
        else { failed++; Console.WriteLine("FAIL " + name); }
    }
    private static void Main()
    {
        Check("idle blocks", !FocusRules.Decide(new DecisionFacts()).Allow);
        var f = new DecisionFacts { InputMatches = true, InputCode = DecisionCode.AllowClickedTarget };
        Check("matched click allows", FocusRules.Decide(f).Code == DecisionCode.AllowClickedTarget);
        f.TestModeOnly = true;
        Check("test mode overrides input", FocusRules.Decide(f).Code == DecisionCode.BlockTestMode);
        Check("accepted user session allows", FocusRules.Decide(new DecisionFacts { UserAuthorized = true }).Allow);
        Check("login compatibility", FocusRules.Decide(new DecisionFacts { WeGameLoginPhase = true }).Allow);
        Check("first startup compatibility", FocusRules.Decide(new DecisionFacts { FirstClientWindow = true }).Allow);
        Check("session unlock compatibility", FocusRules.Decide(new DecisionFacts { JustUnlocked = true }).Allow);
        Check("natural close compatibility", FocusRules.Decide(new DecisionFacts { PrevWindowGone = true }).Allow);
        Check("suppression compatibility", FocusRules.Decide(new DecisionFacts { Suppressed = true }).Allow);
        Check("input overrides suppression", FocusRules.Decide(new DecisionFacts { Suppressed = true, InputMatches = true, InputCode = DecisionCode.AllowClickedTarget }).Code == DecisionCode.AllowClickedTarget);
        Check("unrelated input blocks", !FocusRules.Decide(new DecisionFacts { InputCode = DecisionCode.BlockClickElsewhere }).Allow);
        Check("Alt alone is not switching", !FocusRules.IsKeyboardSwitch(0x12, true, false, false));
        Check("Win alone is not switching", !FocusRules.IsKeyboardSwitch(0x5b, false, true, false));
        Check("Alt Q self cast is not switching", !FocusRules.IsKeyboardSwitch(0x51, true, false, false));
        Check("QWER is not switching", !FocusRules.IsKeyboardSwitch(0x57, false, false, false));
        Check("Alt Tab switches", FocusRules.IsKeyboardSwitch(9, true, false, false));
        Check("Alt Esc switches", FocusRules.IsKeyboardSwitch(27, true, false, false));
        Check("Win number switches", FocusRules.IsKeyboardSwitch(0x31, false, true, false));
        Check("Win Tab switches", FocusRules.IsKeyboardSwitch(9, false, true, false));
        Check("Win D switches", FocusRules.IsKeyboardSwitch(0x44, false, true, false));
        Check("Win arrow only snaps and is not switching", !FocusRules.IsKeyboardSwitch(0x25, false, true, false));
        Check("Win Ctrl arrow switches desktops", FocusRules.IsKeyboardSwitch(0x25, false, true, true));
        Check("Ctrl Esc switches", FocusRules.IsKeyboardSwitch(27, false, false, true));
        Check("Tab in game is not switching", !FocusRules.IsKeyboardSwitch(9, false, false, false));
        Check("game process is exempt", ProcClass.IsGame("League of Legends"));
        Check("game exemption wins over configured target", !ProcClass.IsTarget("League of Legends", "", "", true, true));
        Check("custom targets do not implicitly enable WeGame helpers", !ProcClass.IsTarget("browser", "WeGame", "CefTopWindow", false, false));
        Check("enabled WeGame includes its helpers", ProcClass.IsTarget("browser", "WeGame", "CefTopWindow", false, true));
        Check("client is not match process", !ProcClass.IsGame("LeagueClientUx"));
        Check("WeGame main", ProcClass.IsWeGameWindow("wegame", "", ""));
        Check("WeGame browser helper", ProcClass.IsWeGameWindow("browser", "WeGame", "CefTopWindow"));
        Check("WeGame pallas helper", ProcClass.IsWeGameWindow("pallas", "SIDE_WEB_VIEW", "TWINCONTROL"));
        Check("QQ browser is not WeGame", !ProcClass.IsWeGameWindow("browser", "", "QBCoreAx"));
        Check("browser web page is not WeGame", !ProcClass.IsWeGameWindow("msedge", "WeGame 官网", "Chrome_WidgetWin_1"));
        Check("client classification", ProcClass.IsClient("LeagueClientUx") && ProcClass.IsClient("LeagueClient") && !ProcClass.IsClient("League of Legends"));
        Console.WriteLine("Logic tests: " + passed + " passed, " + failed + " failed");
        Environment.ExitCode = failed == 0 ? 0 : 1;
    }
}
