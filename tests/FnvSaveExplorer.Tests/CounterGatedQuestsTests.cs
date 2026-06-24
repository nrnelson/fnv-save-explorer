using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class CounterGatedQuestsTests
{
    // A VMS16 "Ghost Town Gunfight"-shaped quest: its GameMode completes the quest (SetStage to the QSDT-complete
    // stage 100) only inside `if nGangerDeathCount >= 6`, and the counter is bumped by an external OnDeath script.
    private static QuestDefinition GhostTownGunfight(uint scriptFormId = 0x999) => new(
        FormId: 0x00104EAE,
        Stages:
        [
            new QuestStageDef(70, 0x00, null, null),
            new QuestStageDef(100, 0x01, null, "CompleteQuest VMS16"), // QSDT-complete
        ],
        Objectives: [new QuestObjectiveDef(70, "Defeat the Powder Gangers", [])],
        DataFlags: 0x00,
        Name: "Ghost Town Gunfight",
        Edid: "VMS16",
        ScriptFormId: scriptFormId,
        GameModeScript:
            "if (GetObjectiveDisplayed VMS16 70 == 1) && (GetObjectiveCompleted VMS16 70 == 0)\n" +
            "   if nGangerDeathCount >= 6\n" +
            "      SetObjectiveCompleted VMS16 70 1\n" +
            "      SetStage VMS16 100\n" +
            "   endif\n" +
            "endif",
        LocalVars: ["nGangerDeathCount"]);

    [Fact]
    public void Counter_gate_binds_to_an_external_increment()
    {
        var inc = new CounterIncrement(0x00104C67, "VMS16", "nGangerDeathCount", 1);

        var gate = Assert.Single(CounterGatedQuests.Build([GhostTownGunfight()], [inc]));

        Assert.Equal("nGangerDeathCount", gate.Counter);
        Assert.Equal(">=", gate.Op);
        Assert.Equal(6, gate.Threshold);
        Assert.Equal(100, gate.CompletingStage);
        Assert.True(gate.CompletesQuest);
        Assert.True(gate.Bound);
        Assert.True(gate.ExternallyIncremented);
        Assert.False(gate.SelfIncremented);
        Assert.Equal([0x00104C67u], gate.IncrementScripts);
    }

    [Fact]
    public void Counter_gate_with_no_increment_is_unbound()
    {
        // Same shape but the counter is never incremented anywhere — a do-once/timer flag masquerading as a
        // counter. The gate is still detected (so a probe can surface the decode gap) but reports Bound == false.
        var gate = Assert.Single(CounterGatedQuests.Build([GhostTownGunfight()], []));
        Assert.False(gate.Bound);
        Assert.Empty(gate.IncrementScripts);
    }

    [Fact]
    public void Unconditional_completion_is_not_a_counter_gate()
    {
        // A quest whose GameMode unconditionally SetStages to its complete stage has no counter guard -> no gate.
        var q = new QuestDefinition(
            0x1, [new QuestStageDef(100, 0x01, null, null)], [new QuestObjectiveDef(10, "x", [])],
            Name: "Plain", Edid: "VPlain", GameModeScript: "SetStage VPlain 100", LocalVars: ["nUnused"]);
        Assert.Empty(CounterGatedQuests.Build([q], []));
    }

    [Fact]
    public void External_single_kill_completion_is_detected_and_excludes_the_quests_own_script()
    {
        // "I Fought the Law" shape: an external OnDeath script completes the quest with a single SetStage to its
        // complete stage — the no-counter single-kill bucket-C case.
        var q = new QuestDefinition(
            0x0008D0E3, [new QuestStageDef(100, 0x01, null, null)], [new QuestObjectiveDef(10, "Kill the boss", [])],
            Name: "I Fought the Law", Edid: "VMS02", ScriptFormId: 0xAAA);
        var external = new ExternalQuestEffect(0xBBB, "VMS02", QuestScriptVerb.SetStage, 100, Conditional: false, Block: "ondeath");
        // An effect from the quest's OWN script must be ignored (not "external").
        var own = new ExternalQuestEffect(0xAAA, "VMS02", QuestScriptVerb.CompleteQuest, 0, Conditional: false, Block: "gamemode");

        var comp = Assert.Single(CounterGatedQuests.BuildExternalCompletions([q], [external, own], new HashSet<uint>()));

        Assert.Equal(0x0008D0E3u, comp.QuestFormId);
        Assert.Equal([0xBBBu], comp.Scripts);     // only the external script
        Assert.True(comp.HasUnconditional);
        Assert.True(comp.ViaKill);                 // OnDeath block
        Assert.False(comp.ViaCounter);
    }

    [Fact]
    public void External_setstage_to_a_non_completing_stage_is_not_a_completion()
    {
        var q = new QuestDefinition(
            0x1, [new QuestStageDef(50, 0x00, null, null)], [new QuestObjectiveDef(10, "x", [])],
            Name: "Q", Edid: "VQ", ScriptFormId: 0xAAA);
        var e = new ExternalQuestEffect(0xBBB, "VQ", QuestScriptVerb.SetStage, 50, Conditional: false, Block: "ondeath");
        Assert.Empty(CounterGatedQuests.BuildExternalCompletions([q], [e], new HashSet<uint>()));
    }
}
