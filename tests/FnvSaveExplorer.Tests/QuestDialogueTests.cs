using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>Phase B (ROADMAP §6 #16): the masters' dialogue (DIAL→INFO) result scripts carry the
/// <c>StartQuest</c>/<c>SetStage</c> calls that start the quests no QUST script ever references. These pin the
/// opt-in INFO reader + the PluginDatabase dialogue-target resolution.</summary>
public class QuestDialogueTests
{
    private static byte[] Data(byte flags) => [flags, 0, 0, 0, 0, 0, 0, 0];

    [Fact]
    public void Reads_info_result_script_effects_only_when_asked()
    {
        var bytes = EsmBuilder.PluginWithDialogue(
            [],
            [new TestRecord("QUST", 0x00104EAE, Edid: "VMS16", Full: "Ghost Town Gunfight",
                Subs: [("DATA", Data(0x00))])],
            "StartQuest VMS16\nSetStage VMS16 10");

        using var off = new MemoryStream(bytes);
        Assert.Empty(TesPlugin.Read(off, "A.esm").DialogueEffects); // default: dialogue not read

        using var on = new MemoryStream(bytes);
        var effects = TesPlugin.Read(on, "A.esm", readDialogue: true).DialogueEffects;
        Assert.Contains(effects, e => e.Verb == QuestScriptVerb.StartQuest && e.TargetQuestEdid == "VMS16");
        Assert.Contains(effects, e => e.Verb == QuestScriptVerb.SetStage && e.TargetQuestEdid == "VMS16" && e.Arg1 == 10);
    }

    [Fact]
    public void PluginDatabase_resolves_dialogue_started_quests_to_formids()
    {
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.PluginWithDialogue(
            [],
            [
                new TestRecord("QUST", 0x00104EAE, Edid: "VMS16", Full: "Ghost Town Gunfight", Subs: [("DATA", Data(0x00))]),
                new TestRecord("QUST", 0x00104C1C, Edid: "VCG01", Full: "Ain't That a Kick", Subs: [("DATA", Data(0x01))]),
            ],
            "SetStage VMS16 10")); // only VMS16 is dialogue-started

        var withoutDlg = PluginDatabase.Build(["A.esm"], dir.Path);
        Assert.Empty(withoutDlg.DialogueStartedQuests);

        var withDlg = PluginDatabase.Build(["A.esm"], dir.Path, withDialogue: true);
        Assert.Contains(0x00104EAEu, withDlg.DialogueStartedQuests);
        Assert.DoesNotContain(0x00104C1Cu, withDlg.DialogueStartedQuests); // no INFO targets VCG01
    }
}
