using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class QuestPipboyTests
{
    // QUST DATA subrecord: byte 0 = quest flags (bit0 = Start Game Enabled), rest priority/delay.
    private static byte[] Data(byte flags) => [flags, 0, 0, 0, 0, 0, 0, 0];
    private static byte[] I16(short v) => [(byte)v, (byte)(v >> 8)];
    private static byte[] Z(string s) { var d = Encoding.Latin1.GetBytes(s); var r = new byte[d.Length + 1]; Array.Copy(d, r, d.Length); return r; }

    private static QuestPipboy Compute(params TestRecord[] qusts)
    {
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([], qusts));
        var save = FalloutSave.Parse(QuestSave.Build());
        var db = PluginDatabase.Build(save.Plugins, dir.Path);
        return QuestPipboy.Compute(save, db);
    }

    [Fact]
    public void Start_game_enabled_quest_shows_active_with_its_displayed_objective()
    {
        // SGE quest seeded at its lowest stage (10), whose result script displays objective 10.
        var pip = Compute(new TestRecord("QUST", 0x00100001, Edid: "QONE", Full: "Quest One", Subs:
        [
            ("DATA", Data(0x01)),                                  // Start Game Enabled
            ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QONE 10 1")),
            ("QOBJ", I16(10)), ("NNAM", Z("Do the thing")),
        ]));

        var q = Assert.Single(pip.Quests);
        Assert.Equal("Quest One", q.Name);
        Assert.Equal(PipboyQuestState.Active, q.State);
        var obj = Assert.Single(q.Objectives);
        Assert.Equal(10, obj.Index);
        Assert.False(obj.Completed);
    }

    [Fact]
    public void Quest_that_is_never_started_is_excluded_even_with_a_displayable_objective()
    {
        // The "Welcome to the Big Empty" precision case: a non-SGE quest whose masters define a displayed-objective
        // stage, but nothing starts it -> not running -> NOT shown. (This is what a raw save read gets wrong.)
        var pip = Compute(new TestRecord("QUST", 0x00100002, Edid: "QTWO", Full: "Quest Two", Subs:
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
        // SGE quest A starts at stage 10 (displays obj 10), then non-conditionally SetStages itself to 20, which
        // completes obj 10 and CompleteQuests -> A reads Completed. A also StartQuests + SetStages B to 10, which
        // displays B's objective -> B reads Active even though B is not Start-Game-Enabled (the VCG01->VMQ01 hand-off).
        var a = new TestRecord("QUST", 0x00100003, Edid: "QA", Full: "Quest A", Subs:
        [
            ("DATA", Data(0x01)),
            ("INDX", I16(10)), ("QSDT", [0x00]),
                ("SCTX", Z("SetObjectiveDisplayed QA 10 1\nSetStage QA 20\nStartQuest QB\nSetStage QB 10")),
            ("INDX", I16(20)), ("QSDT", [0x00]),
                ("SCTX", Z("SetObjectiveCompleted QA 10 1\nCompleteQuest QA")),
            ("QOBJ", I16(10)), ("NNAM", Z("A objective")),
        ]);
        var b = new TestRecord("QUST", 0x00100004, Edid: "QB", Full: "Quest B", Subs:
        [
            ("DATA", Data(0x00)),                                  // not SGE — only reachable via A's hand-off
            ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QB 10 1")),
            ("QOBJ", I16(10)), ("NNAM", Z("B objective")),
        ]);

        var pip = Compute(a, b);

        var qa = Assert.Single(pip.Quests, x => x.Name == "Quest A");
        Assert.Equal(PipboyQuestState.Completed, qa.State);
        Assert.True(Assert.Single(qa.Objectives).Completed);

        var qb = Assert.Single(pip.Quests, x => x.Name == "Quest B");
        Assert.Equal(PipboyQuestState.Active, qb.State);   // started purely by A's non-conditional hand-off
    }

    [Fact]
    public void Conditional_cross_quest_setstage_is_not_followed()
    {
        // An if-guarded SetStage to another quest must NOT start it (Phase-A avoids over-firing on conditions).
        var a = new TestRecord("QUST", 0x00100005, Edid: "QC", Full: "Quest C", Subs:
        [
            ("DATA", Data(0x01)),
            ("INDX", I16(10)), ("QSDT", [0x00]),
                ("SCTX", Z("SetObjectiveDisplayed QC 10 1\nif SomeVar == 1\nSetStage QD 10\nendif")),
            ("QOBJ", I16(10)), ("NNAM", Z("C objective")),
        ]);
        var d = new TestRecord("QUST", 0x00100006, Edid: "QD", Full: "Quest D", Subs:
        [
            ("DATA", Data(0x00)),
            ("INDX", I16(10)), ("QSDT", [0x00]), ("SCTX", Z("SetObjectiveDisplayed QD 10 1")),
            ("QOBJ", I16(10)), ("NNAM", Z("D objective")),
        ]);

        var pip = Compute(a, d);

        Assert.Single(pip.Quests);                          // only Quest C
        Assert.DoesNotContain(pip.Quests, x => x.Name == "Quest D");
    }
}
