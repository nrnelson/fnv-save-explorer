using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class QuestPipboyTests
{
    // QUST DATA subrecord: byte 0 = quest flags (bit0 = Start Game Enabled), rest priority/delay.
    private static byte[] Data(byte flags) => [flags, 0, 0, 0, 0, 0, 0, 0];
    private static byte[] I16(short v) => [(byte)v, (byte)(v >> 8)];
    private static byte[] U32(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static byte[] Z(string s) { var d = Encoding.Latin1.GetBytes(s); var r = new byte[d.Length + 1]; Array.Copy(d, r, d.Length); return r; }

    // A SCPT record whose GameMode block is the given body — the quest's startup script (ROADMAP §6 #16).
    private static TestRecord Scpt(uint formId, string gameModeBody) =>
        new("SCPT", formId, Edid: $"S{formId:X}", Full: null, Subs: [("SCTX", Z($"Begin GameMode\n{gameModeBody}\nEnd"))]);

    private static QuestPipboy Compute(params TestRecord[] records) => Compute(QuestSave.Build(), records);

    private static QuestPipboy Compute(byte[] saveBytes, params TestRecord[] records)
    {
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([], records));
        var save = FalloutSave.Parse(saveBytes);
        var db = PluginDatabase.Build(save.Plugins, dir.Path);
        return QuestPipboy.Compute(save, db);
    }

    // Builds masters that include dialogue INFO result scripts (Phase B) and computes against the given save.
    private static QuestPipboy ComputeWithDialogue(byte[] saveBytes, IEnumerable<TestRecord> records, params (uint FormId, string Script)[] infos)
    {
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.PluginWithDialogue([], records, infos));
        var save = FalloutSave.Parse(saveBytes);
        var db = PluginDatabase.Build(save.Plugins, dir.Path, withDialogue: true);
        return QuestPipboy.Compute(save, db);
    }

    // Builds masters whose dialogue INFO records are supplied directly (so they can carry a CTDA condition).
    private static QuestPipboy ComputeWithDialogueInfos(byte[] saveBytes, IEnumerable<TestRecord> records, params TestRecord[] infoRecords)
    {
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.PluginWithDialogueInfos([], records, infoRecords));
        var save = FalloutSave.Parse(saveBytes);
        var db = PluginDatabase.Build(save.Plugins, dir.Path, withDialogue: true);
        return QuestPipboy.Compute(save, db);
    }

    // A quest started+shown by a said-INFO, with a completing (QSDT 0x01) stage at 100. Used by the CTDA tests.
    private static TestRecord CompletableQuest() => new("QUST", 0x00100030, Edid: "QCMP", Full: "Completable Quest", Subs:
    [
        ("DATA", Data(0x00)),
        ("INDX", I16(5)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QCMP 5 1")),
        ("INDX", I16(100)), ("QSDT", [0x01]),                          // completing stage (never reached by SetStage)
        ("QOBJ", I16(5)), ("NNAM", Z("Do the thing")),
    ]);

    [Fact]
    public void Said_info_ctda_precondition_completes_a_running_quest()
    {
        // ROADMAP §6 #16 CTDA: said-INFO 0x0010A001 (present in QuestSave.Build) carries GetStage QCMP >= 100 — its
        // completing stage — proving QCMP reached completion. QCMP is started+shown active by said-INFO 0x0010A050;
        // the CTDA reclassifies it from active to completed.
        var pip = ComputeWithDialogueInfos(QuestSave.Build(), [CompletableQuest()],
            new TestRecord("INFO", 0x0010A050, null, null, Subs: [("SCTX", Z("SetStage QCMP 5"))]),
            new TestRecord("INFO", 0x0010A001, null, null,
                Subs: [("CTDA", EsmBuilder.Ctda(op: 3, compareValue: 100, function: (uint)QuestFunction.GetStage, param1: 0x00100030))]));

        var q = Assert.Single(pip.Quests, x => x.Name == "Completable Quest");
        Assert.Equal(PipboyQuestState.Completed, q.State);
    }

    [Fact]
    public void Said_info_ctda_below_completing_stage_leaves_quest_active()
    {
        // Guard: GetStage QCMP >= 50 (below the completing stage 100) proves nothing about completion — stays active.
        var pip = ComputeWithDialogueInfos(QuestSave.Build(), [CompletableQuest()],
            new TestRecord("INFO", 0x0010A050, null, null, Subs: [("SCTX", Z("SetStage QCMP 5"))]),
            new TestRecord("INFO", 0x0010A001, null, null,
                Subs: [("CTDA", EsmBuilder.Ctda(op: 3, compareValue: 50, function: (uint)QuestFunction.GetStage, param1: 0x00100030))]));

        var q = Assert.Single(pip.Quests, x => x.Name == "Completable Quest");
        Assert.Equal(PipboyQuestState.Active, q.State);
    }

    [Fact]
    public void Ctda_completion_does_not_surface_a_quest_that_is_not_running()
    {
        // Precision guard: the CTDA completion only RECLASSIFIES already-running quests. With no said-INFO starting
        // QCMP, the GetStage>=100 condition must NOT pull it into the Pip-Boy (no add, only reclassify).
        var pip = ComputeWithDialogueInfos(QuestSave.Build(), [CompletableQuest()],
            new TestRecord("INFO", 0x0010A001, null, null,
                Subs: [("CTDA", EsmBuilder.Ctda(op: 3, compareValue: 100, function: (uint)QuestFunction.GetStage, param1: 0x00100030))]));

        Assert.DoesNotContain(pip.Quests, x => x.Name == "Completable Quest");
    }

    private static TestRecord DialogueQuest() => new("QUST", 0x00100010, Edid: "QDLG", Full: "Dialogue Quest", Subs:
    [
        ("DATA", Data(0x00)),                                          // not SGE, not a QUST-propagation target
        ("INDX", I16(5)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QDLG 5 1")),
        ("QOBJ", I16(5)), ("NNAM", Z("Do the dialogue thing")),
    ]);

    [Fact]
    public void Dialogue_started_quest_shows_when_its_said_info_is_present_in_the_save()
    {
        // ROADMAP §6 #16 Phase B step 2: INFO 0x0010A050 (which SetStages QDLG) is present as a change form in
        // QuestSave.Build() — i.e. the player said that line — so the save-gated dialogue seed starts QDLG.
        var pip = ComputeWithDialogue(QuestSave.Build(), [DialogueQuest()], (0x0010A050u, "SetStage QDLG 5"));

        var q = Assert.Single(pip.Quests, x => x.Name == "Dialogue Quest");
        Assert.Equal(PipboyQuestState.Active, q.State);
        Assert.Equal(5, Assert.Single(q.Objectives).Index);
    }

    [Fact]
    public void Dialogue_objective_managed_quest_shows_via_said_info_objective_effects()
    {
        // "High Times" / "I Put a Spell on You" case: a quest with NO objective-bearing stage (only a fail stage) —
        // its objectives are displayed directly by dialogue. A present said-INFO that starts it AND displays an
        // objective must surface it active with that objective (the stage reach alone shows nothing).
        var quest = new TestRecord("QUST", 0x00100020, Edid: "QOBJ2", Full: "Objective Dialogue Quest", Subs:
        [
            ("DATA", Data(0x00)),
            ("INDX", I16(255)), ("QSDT", [0x02]),                     // only a fail stage — no objective-bearing stage
            ("QOBJ", I16(10)), ("NNAM", Z("Do the dialogue-managed thing")),
        ]);
        var pip = ComputeWithDialogue(QuestSave.Build(), [quest],
            (0x0010A050u, "StartQuest QOBJ2\nSetObjectiveDisplayed QOBJ2 10 1"));

        var q = Assert.Single(pip.Quests, x => x.Name == "Objective Dialogue Quest");
        Assert.Equal(PipboyQuestState.Active, q.State);
        Assert.Equal(10, Assert.Single(q.Objectives).Index);
    }

    [Fact]
    public void Dialogue_objective_effect_does_not_resurface_a_completed_quest()
    {
        // The "By a Campfire on the Trail" guard: a quest that a said-INFO completes/stops must NOT be resurfaced by a
        // later said line's objective effect (it has dropped off the Pip-Boy). Quest has no objective-bearing stage.
        var quest = new TestRecord("QUST", 0x00100021, Edid: "QGONE", Full: "Resolved Quest", Subs:
        [
            ("DATA", Data(0x00)),
            ("INDX", I16(255)), ("QSDT", [0x02]),
            ("QOBJ", I16(10)), ("NNAM", Z("Should not resurface")),
        ]);
        var pip = ComputeWithDialogue(QuestSave.Build(), [quest],
            (0x0010A050u, "StartQuest QGONE\nStopQuest QGONE\nSetObjectiveDisplayed QGONE 10 1"));

        // Started + stopped + an objective line said, but it's completed → no displayed objective is applied → it does
        // not appear as a stray completed entry with an objective.
        Assert.DoesNotContain(pip.Quests, x => x.Name == "Resolved Quest");
    }

    [Fact]
    public void Dialogue_stopped_quest_shows_completed_greyed()
    {
        // "Back in the Saddle" case: one said-INFO starts + stages the quest, another said-INFO StopQuests it (the
        // tutorial's end). Both INFOs are present in the save -> the quest is started, reached its display stage, and
        // stopped -> it shows in the Pip-Boy's completed (greyed) section. Both 0x0010A050 and 0x0010A001 are change
        // forms in QuestSave.Build().
        var pip = ComputeWithDialogue(QuestSave.Build(), [DialogueQuest()],
            (0x0010A050u, "StartQuest QDLG\nSetStage QDLG 5"),
            (0x0010A001u, "StopQuest QDLG"));

        var q = Assert.Single(pip.Quests, x => x.Name == "Dialogue Quest");
        Assert.Equal(PipboyQuestState.Completed, q.State);
    }

    [Fact]
    public void Dialogue_started_quest_is_excluded_when_its_info_is_absent_from_the_save()
    {
        // The precision guarantee: INFO 0x0010A099 is NOT a change form in the save (the line was never said), so the
        // quest is NOT surfaced — this is what keeps background-initialized quests (whose start dialogue never fired)
        // out of the computed Pip-Boy list.
        var pip = ComputeWithDialogue(QuestSave.Build(), [DialogueQuest()], (0x0010A099u, "SetStage QDLG 5"));

        Assert.DoesNotContain(pip.Quests, x => x.Name == "Dialogue Quest");
    }

    // The Goodsprings chargen quest VCG01 "Ain't That a Kick" is FormId 0x0010A001 here, matching the formType-7
    // change form QuestSave can emit. Its completing stage 200 hands off to the chained quest VMQ01.
    private const uint ChargenFormId = 0x0010A001;

    private static TestRecord ChargenQuest() => new("QUST", ChargenFormId, Edid: "VCG01", Full: "Ain't That a Kick", Subs:
    [
        ("DATA", Data(0x11)),                                          // Start-Game-Enabled (as the real VCG01)
        // Mirror VCG01's real objective quirks: obj 10 displayed+completed; obj 30 SetObjectiveCompleted only (never
        // explicitly displayed); obj 40 SetObjectiveDisplayed only (never explicitly completed). All three must show
        // for the completed quest, all ticked (the engine greys a completed quest's objectives).
        ("INDX", I16(10)), ("QSDT", [0x00]),
            ("SCTX", Z("SetObjectiveDisplayed VCG01 10 1\nSetObjectiveCompleted VCG01 10 1\nSetObjectiveCompleted VCG01 30 1\nSetObjectiveDisplayed VCG01 40 1")),
        ("INDX", I16(200)), ("QSDT", [0x01]),                          // completing stage: hands off to VMQ01
            ("SCTX", Z("StartQuest VMQ01\nSetStage VMQ01 10")),
        ("QOBJ", I16(10)), ("NNAM", Z("Walk to the Vit-o-matic Vigor Tester")),
        ("QOBJ", I16(30)), ("NNAM", Z("Use the Vit-o-matic Vigor Tester")),
        ("QOBJ", I16(40)), ("NNAM", Z("Follow Doc Mitchell to the exit")),
    ]);

    private static TestRecord HandoffQuest() => new("QUST", 0x0010A002, Edid: "VMQ01", Full: "They Went That-a-Way", Subs:
    [
        ("DATA", Data(0x00)),                                          // not SGE — only reachable via the hand-off
        ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed VMQ01 10 1")),
        ("QOBJ", I16(10)), ("NNAM", Z("Find the men who tried to kill you")),
    ]);

    [Fact]
    public void FormType7_quest_with_completed_pattern_is_completed_and_propagates_to_its_chain()
    {
        // ROADMAP §6 #16 save-anchored seed: VCG01's formType-7 change form matches the validated "completed" pattern
        // 0xC0000000 (bit31 + bit30) -> its completing stage ran -> VCG01 completed AND stage 200 hands off
        // (StartQuest VMQ01 + SetStage VMQ01 10) -> VMQ01 active.
        var save = QuestSave.Build(ft7FormId: ChargenFormId, ft7Flags: 0xC0000000);
        var pip = Compute(save, ChargenQuest(), HandoffQuest());

        var vcg01 = Assert.Single(pip.Quests, q => q.Name == "Ain't That a Kick");
        Assert.Equal(PipboyQuestState.Completed, vcg01.State);
        // All three objectives show (incl. obj 30 which was only SetObjectiveCompleted), all ticked, descending index.
        Assert.Equal([40, 30, 10], vcg01.Objectives.Select(o => o.Index));
        Assert.All(vcg01.Objectives, o => Assert.True(o.Completed));

        var vmq01 = Assert.Single(pip.Quests, q => q.Name == "They Went That-a-Way");
        Assert.Equal(PipboyQuestState.Active, vmq01.State);            // started purely by the formType-7 hand-off
    }

    [Fact]
    public void FormType7_quest_not_yet_completed_does_not_fire_the_chain()
    {
        // The in-Doc-Mitchell's-house state: VCG01's record is 0x80000000 (bit30 not yet added). Nothing reaches the
        // completing stage, so neither the chargen quest nor its hand-off is shown.
        var save = QuestSave.Build(ft7FormId: ChargenFormId, ft7Flags: 0x80000000);
        var pip = Compute(save, ChargenQuest(), HandoffQuest());

        Assert.DoesNotContain(pip.Quests, q => q.Name == "Ain't That a Kick");
        Assert.DoesNotContain(pip.Quests, q => q.Name == "They Went That-a-Way");
    }

    [Fact]
    public void FormType7_objective_record_family_0x60000000_does_not_fire_the_seed()
    {
        // Regression for the corpus-validated over-firing case: a player-facing formType-7 quest can also carry a
        // DIFFERENT objective-state record flagged 0x60000000 (bit29 + bit30) — e.g. "Ant Misbehavin'"
        // (VNellisInfestation) carries this throughout its life across the whole corpus. bit30 is set there too, so a
        // bit30-only gate would wrongly mark it completed; the tightened pattern (bits 30+31 set, bit29 clear) must
        // NOT fire on it.
        var save = QuestSave.Build(ft7FormId: ChargenFormId, ft7Flags: 0x60000000);
        var pip = Compute(save, ChargenQuest(), HandoffQuest());

        Assert.DoesNotContain(pip.Quests, q => q.Name == "Ain't That a Kick");
        Assert.DoesNotContain(pip.Quests, q => q.Name == "They Went That-a-Way");
    }

    [Fact]
    public void Start_game_enabled_quest_shows_active_when_its_gamemode_sets_its_startup_stage()
    {
        // SGE quest whose GameMode startup script sets its lowest stage (10), whose result script displays obj 10.
        var pip = Compute(
            new TestRecord("QUST", 0x00100001, Edid: "QONE", Full: "Quest One", Subs:
            [
                ("DATA", Data(0x01)), ("SCRI", U32(0x00100011)),
                ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QONE 10 1")),
                ("QOBJ", I16(10)), ("NNAM", Z("Do the thing")),
            ]),
            Scpt(0x00100011, "SetStage QONE 10"));

        var q = Assert.Single(pip.Quests);
        Assert.Equal("Quest One", q.Name);
        Assert.Equal(PipboyQuestState.Active, q.State);
        var obj = Assert.Single(q.Objectives);
        Assert.Equal(10, obj.Index);
        Assert.False(obj.Completed);
    }

    [Fact]
    public void Sge_quest_whose_gamemode_only_catcher_sets_a_late_stage_is_not_shown()
    {
        // The over-fire case: an SGE quest with a displayable startup stage (10) but whose GameMode only sets a
        // LATE catcher stage (90). The startup stage isn't reached (it's set by an external trigger), so nothing
        // is displayed -> NOT shown. (This is what pruned Save 57 from 15 down to 8.)
        var pip = Compute(
            new TestRecord("QUST", 0x00100002, Edid: "QLATE", Full: "Quest Late", Subs:
            [
                ("DATA", Data(0x01)), ("SCRI", U32(0x00100012)),
                ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QLATE 10 1")),
                ("INDX", I16(90)), ("QSDT", [0x01]),
                ("QOBJ", I16(10)), ("NNAM", Z("Startup objective")),
            ]),
            Scpt(0x00100012, "if SomeCatcher\nSetStage QLATE 90\nendif"));

        Assert.Empty(pip.Quests);
    }

    [Fact]
    public void Quest_that_is_never_started_is_excluded_even_with_a_displayable_objective()
    {
        // The "Welcome to the Big Empty" precision case: a non-SGE quest whose masters define a displayed-objective
        // stage, but nothing starts it -> not running -> NOT shown. (This is what a raw save read gets wrong.)
        var pip = Compute(new TestRecord("QUST", 0x00100003, Edid: "QTWO", Full: "Quest Two", Subs:
        [
            ("DATA", Data(0x00)),                                  // NOT Start Game Enabled
            ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QTWO 10 1")),
            ("QOBJ", I16(10)), ("NNAM", Z("Should not show")),
        ]));

        Assert.Empty(pip.Quests);
    }

    [Fact]
    public void Cross_quest_setstage_and_completequest_drive_propagation_and_completion()
    {
        // SGE quest A's GameMode starts it at stage 10 (displays obj 10), whose result script non-conditionally
        // SetStages A to 20 (completes obj 10, CompleteQuests A -> Completed) and StartQuests + SetStages B to 10,
        // which displays B's objective -> B reads Active though not SGE (the VCG01->VMQ01 hand-off).
        var a = new TestRecord("QUST", 0x00100004, Edid: "QA", Full: "Quest A", Subs:
        [
            ("DATA", Data(0x01)), ("SCRI", U32(0x00100014)),
            ("INDX", I16(10)), ("QSDT", [0x00]),
                ("SCTX", Z("SetObjectiveDisplayed QA 10 1\nSetStage QA 20\nStartQuest QB\nSetStage QB 10")),
            ("INDX", I16(20)), ("QSDT", [0x00]),
                ("SCTX", Z("SetObjectiveCompleted QA 10 1\nCompleteQuest QA")),
            ("QOBJ", I16(10)), ("NNAM", Z("A objective")),
        ]);
        var b = new TestRecord("QUST", 0x00100005, Edid: "QB", Full: "Quest B", Subs:
        [
            ("DATA", Data(0x00)),                                  // not SGE — only reachable via A's hand-off
            ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QB 10 1")),
            ("QOBJ", I16(10)), ("NNAM", Z("B objective")),
        ]);

        var pip = Compute(a, b, Scpt(0x00100014, "SetStage QA 10"));

        var qa = Assert.Single(pip.Quests, x => x.Name == "Quest A");
        Assert.Equal(PipboyQuestState.Completed, qa.State);
        Assert.True(Assert.Single(qa.Objectives).Completed);

        var qb = Assert.Single(pip.Quests, x => x.Name == "Quest B");
        Assert.Equal(PipboyQuestState.Active, qb.State);   // started purely by A's non-conditional hand-off
    }

    [Fact]
    public void Conditional_cross_quest_setstage_is_not_followed()
    {
        // An if-guarded SetStage to ANOTHER quest must NOT start it (Phase-A avoids over-firing cross-quest).
        var a = new TestRecord("QUST", 0x00100006, Edid: "QC", Full: "Quest C", Subs:
        [
            ("DATA", Data(0x01)), ("SCRI", U32(0x00100016)),
            ("INDX", I16(10)), ("QSDT", [0x00]),
                ("SCTX", Z("SetObjectiveDisplayed QC 10 1\nif SomeVar == 1\nSetStage QD 10\nendif")),
            ("QOBJ", I16(10)), ("NNAM", Z("C objective")),
        ]);
        var d = new TestRecord("QUST", 0x00100007, Edid: "QD", Full: "Quest D", Subs:
        [
            ("DATA", Data(0x00)),
            ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QD 10 1")),
            ("QOBJ", I16(10)), ("NNAM", Z("D objective")),
        ]);

        var pip = Compute(a, d, Scpt(0x00100016, "SetStage QC 10"));

        Assert.Single(pip.Quests);                          // only Quest C
        Assert.DoesNotContain(pip.Quests, x => x.Name == "Quest D");
    }
}
