using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class ScriptStartupTests
{
    private static ScriptStartup Analyze(string gameMode, params string[] locals) =>
        ScriptStartup.Analyze(gameMode, locals);

    [Fact]
    public void No_guard_always_holds()
    {
        var s = ScriptStartup.Analyze("", []);
        Assert.True(s.GuardHolds(null));
        Assert.True(s.GuardHolds("   "));
    }

    [Fact]
    public void World_query_uses_its_game_start_default_of_zero()
    {
        var s = ScriptStartup.Analyze("", []);
        // A default-enabled reference: GetDisabled == 0 holds; progress/rep queries do not.
        Assert.True(s.GuardHolds("RNVTARef.GetDisabled == 0"));
        Assert.False(s.GuardHolds("GetReputationThreshold RepNVCaesarsLegion 1 >= 2"));
        Assert.False(s.GuardHolds("GetStage SomeQuest >= 80"));
        Assert.False(s.GuardHolds("GetObjectiveDisplayed SomeQuest 30")); // bare world truthiness
        Assert.False(s.GuardHolds("vStoryEventBennyKilledCasino == 1"));  // a global, default 0
    }

    [Fact]
    public void Do_once_flag_reaches_its_set_value_so_a_later_guard_is_satisfiable()
    {
        // The intro idiom: a do-once flag flipped to 1 under a script-internal guard, then read by a later guard.
        var s = Analyze("if ( DoOnceMessage == 0 )\nset DoOnceMessage to 1\nendif", "DoOnceMessage");
        Assert.True(s.GuardHolds("DoOnceMessage == 1"));   // reachable {0,1}
        Assert.True(s.GuardHolds("DoOnceMessage == 0"));   // still reachable
        Assert.False(s.GuardHolds("DoOnceMessage == 5"));  // never assigned
    }

    [Fact]
    public void A_local_incremented_only_under_a_world_guard_stays_zero_at_startup()
    {
        // The "Don't Tread on the Bear!" false-positive case: iHouseObjective is bumped only when a House quest
        // completes (a world guard), so at startup it never reaches 1 and the objective-display guard fails.
        var s = Analyze(
            "if ( VDialogueMrHouse.BoomerQuest == 3 )\nset iHouseObjective to 1\nendif",
            "iHouseObjective");
        Assert.False(s.GuardHolds("iHouseObjective == 1"));
        Assert.True(s.GuardHolds("iHouseObjective == 0"));
    }

    [Fact]
    public void A_timer_decremented_each_frame_is_dynamic_so_its_countdown_guard_holds()
    {
        // StartTimer set to 30 then decremented by GetSecondsPassed -> dynamic -> "StartTimer <= 0" is reachable.
        var s = Analyze(
            "set StartTimer to 30\nset StartTimer to ( StartTimer - GetSecondsPassed )",
            "StartTimer");
        Assert.True(s.GuardHolds("StartTimer <= 0"));
        Assert.True(s.GuardHolds("StartTimer >= 100")); // dynamic admits any comparison
    }

    [Fact]
    public void And_or_and_parens_combine_atoms()
    {
        var s = Analyze("if ( Flag == 0 )\nset Flag to 1\nendif", "Flag");
        Assert.True(s.GuardHolds("( Flag == 1 ) && ( GetDisabled == 0 )"));   // both hold
        Assert.False(s.GuardHolds("( Flag == 1 ) && ( GetQuestCompleted X )")); // second is world/false
        Assert.True(s.GuardHolds("( Flag == 5 ) || ( Flag == 1 )"));          // second disjunct holds
    }
}
