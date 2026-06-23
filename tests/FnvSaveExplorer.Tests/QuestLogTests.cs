using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class QuestLogTests
{
    [Fact]
    public void Reads_stage_list_objectives_and_target_enable_state()
    {
        // Masters: one QUST with two stages (stage 10 flagged "complete quest", stage 20 not) and one
        // objective whose target is the placed ref 0x0010A050.
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([],
            new TestRecord("QUST", 0x0010A001, Edid: "TestQuest", Full: "Test Quest", Subs:
            [
                ("INDX", I16(10)), ("QSDT", [0x02]), ("CNAM", Z("Find the thing")),
                ("INDX", I16(20)), ("QSDT", [0x00]),
                ("QOBJ", I32(10)), ("NNAM", Z("Do the objective")), ("QSTA", U32(0x0010A050)),
            ])));

        var save = FalloutSave.Parse(QuestSave.Build());
        var db = PluginDatabase.Build(save.Plugins, dir.Path);

        var log = QuestLog.Read(save, db);
        var quest = Assert.Single(log.Quests);

        Assert.Equal(0x0010A001u, quest.FormId);
        Assert.Equal("Test Quest", quest.Name);
        Assert.Equal(QuestState.Completed, quest.State); // stage 10 is done and flagged complete

        var s10 = quest.Stages.Single(s => s.Index == 10);
        Assert.True(s10.Done);
        Assert.Equal(12345u, s10.CompletionTime);
        Assert.Equal("Find the thing", s10.LogText);
        Assert.False(quest.Stages.Single(s => s.Index == 20).Done);

        var obj = Assert.Single(quest.Objectives);
        Assert.Equal(10, obj.Index);
        Assert.Equal("Do the objective", obj.Text);
        Assert.True(obj.Displayed);   // status bit0 (the change form's objectives block records status 1)
        Assert.False(obj.Completed);  // status bit1 not set
        Assert.True(obj.Active);      // displayed and not completed
        var target = Assert.Single(obj.Targets);
        Assert.Equal(0x0010A050u, target.FormId);
        Assert.Equal(EnableState.Enabled, target.State);
    }

    [Fact]
    public void Completed_objective_status_marks_the_quest_completed()
    {
        // Objective status 3 = displayed + completed (bit0|bit1). With its only displayed objective completed,
        // the quest reads Completed. (Confirmed semantics — Saves 56→57 flipped an objective's status 1 → 3.)
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([],
            new TestRecord("QUST", 0x0010A001, Edid: "TestQuest", Full: "Test Quest", Subs:
            [("INDX", I16(10)), ("QSDT", [0x00]), ("QOBJ", I32(10)), ("NNAM", Z("Do the objective"))])));

        var save = FalloutSave.Parse(QuestSave.Build(objStatus: 3)); // displayed + completed
        var log = QuestLog.Read(save, PluginDatabase.Build(save.Plugins, dir.Path));

        var quest = Assert.Single(log.Quests);
        var obj = Assert.Single(quest.Objectives);
        Assert.True(obj.Displayed);
        Assert.True(obj.Completed);
        Assert.False(obj.Active);                       // completed -> not the current objective
        Assert.Equal(QuestState.Completed, quest.State); // all displayed objectives completed
    }

    [Fact]
    public void Quest_with_no_stage_or_objective_state_is_omitted()
    {
        // OBJECTIVES flag set but the objective left at status 0 (not displayed, not completed) and no stage list
        // -> nothing decodable to show, so it isn't listed. (There is no "Pip-Boy" gate: in-game visibility isn't
        // recoverable from the save — a started quest and a background-initialized one share identical bytes.)
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([],
            new TestRecord("QUST", 0x0010A001, Edid: "Q", Full: "Q", Subs:
            [("QOBJ", I32(10)), ("NNAM", Z("obj"))])));

        var save = FalloutSave.Parse(QuestSave.Build(questFlags: 0x20000000, objStatus: 0)); // OBJECTIVES only, status 0
        var log = QuestLog.Read(save, PluginDatabase.Build(save.Plugins, dir.Path));

        Assert.Empty(log.Quests);
    }

    [Fact]
    public void Reading_the_quest_log_does_not_perturb_the_save()
    {
        // The reader is read-only: a round-trip after reading must stay byte-identical (retention invariant).
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([],
            new TestRecord("QUST", 0x0010A001, Edid: "TestQuest", Full: "Test Quest", Subs:
            [("QOBJ", I32(10)), ("NNAM", Z("Obj")), ("QSTA", U32(0x0010A050))])));

        var original = QuestSave.Build();
        var save = FalloutSave.Parse(original);
        _ = QuestLog.Read(save, PluginDatabase.Build(save.Plugins, dir.Path));

        Assert.Equal(original, save.ToBytes());
    }

    [Fact]
    public void Pure_script_quests_with_no_stages_or_objectives_are_omitted()
    {
        // A QUST change form with no decoded stages and a masters definition that has no objectives is a
        // dialogue/timer/script quest — it carries nothing to show, so it isn't a quest-log entry.
        using var dir = new TempDataFolder();
        dir.Write("A.esm", EsmBuilder.Plugin([],
            new TestRecord("QUST", 0x0010A001, Edid: "DialogueQuest", Full: "Dialogue Quest"))); // no Subs

        var save = FalloutSave.Parse(QuestSave.Build(questFlags: 0x40000000)); // SCRIPT only, no STAGES
        var log = QuestLog.Read(save, PluginDatabase.Build(save.Plugins, dir.Path));

        Assert.Empty(log.Quests);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_read_the_quest_log_without_throwing(string path)
    {
        var save = FalloutSave.Load(path);
        var db = PluginDatabase.ForSave(save, null, GameDataLocator.FindMo2Mods(path));
        if (db.Count == 0)
            return; // game masters not available on this machine — skip

        var log = QuestLog.Read(save, db); // must not throw on any real save

        // Every listed quest must have something to show, and any decoded stage index must be sane (0..255).
        foreach (var q in log.Quests)
        {
            Assert.True(q.Stages.Count > 0 || q.Objectives.Count > 0);
            Assert.All(q.Stages, s => Assert.InRange(s.Index, 0, 255));
        }
    }

    private static byte[] I16(short v) { var t = new byte[2]; BinaryPrimitives.WriteInt16LittleEndian(t, v); return t; }
    private static byte[] I32(int v) { var t = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); return t; }
    private static byte[] U32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); return t; }
    private static byte[] Z(string s) { var d = Encoding.Latin1.GetBytes(s); var r = new byte[d.Length + 1]; Array.Copy(d, r, d.Length); return r; }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose body carries a File Location Table, a FormID array, and two
/// change forms: a QUST stage-list form (refID → FormID 0x0010A001) and an objective target reference
/// (0x0010A050) whose change form clears the Initially-Disabled base flag. Lets the quest-log reader's stage
/// parse + objective enable-state be exercised deterministically without a real save (ROADMAP §6 #10).
/// </summary>
internal static class QuestSave
{
    public static byte[] Build(uint questFlags = 0xA0000000, int objStatus = 1,
        uint? ft7FormId = null, uint ft7Flags = 0)
    {
        var b = new List<byte>();
        void Str(string s) => b.AddRange(Encoding.Latin1.GetBytes(s));
        void Delim() => b.Add(0x7C);
        void U16(ushort v) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); b.AddRange(t); }
        void U32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); b.AddRange(t); }
        void StringField(string s) { U16((ushort)s.Length); Delim(); Str(s); Delim(); }

        Str("FO3SAVEGAME");
        var headerSizeAt = b.Count;
        U32(0);            // saveHeaderSize placeholder (patched below)
        U32(0x30);         // version
        Delim();
        Str("ENGLISH"); Delim();
        U32(4); Delim();   // width
        U32(2); Delim();   // height
        U32(7); Delim();   // save number
        StringField("Test");
        StringField("Title");
        U32(3); Delim();   // level
        StringField("Vault1");
        StringField("000.01.02");

        var screenshotStart = b.Count;
        for (var i = 0; i < 4 * 2 * 3; i++) b.Add((byte)i);

        b.Add(0x15);       // trailer
        U32(0);            // pluginStructSize
        b.Add(1); Delim(); // one plugin
        StringField("A.esm");

        // ---- Body: File Location Table, FormID array, then the two change forms ----
        var bodyStart = b.Count;
        var formIdArrayOffset = bodyStart + 32;             // right after the 8-u32 FLT
        uint[] formIds =
        [
            0x00000007,           // iref 0
            0x0010A001,           // iref 1 -> the quest
            0x0010A050,           // iref 2 -> the objective's target reference
            .. (ft7FormId is { } f7 ? new[] { f7 } : []), // iref 3 -> optional formType-7 quest record (ROADMAP §6 #16)
        ];
        var changeFormsOffset = formIdArrayOffset + 4 + formIds.Length * 4;

        // Quest stage-list change form (formType 9, u16 length): a header field then two stage entries —
        // [stageNum][7C][done][7C][04][7C][stageIdx][7C][done2][7C]([u32 time][7C] when done).
        var quest = new List<byte>();
        void QB(params byte[] xs) => quest.AddRange(xs);
        void QU32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); quest.AddRange(t); }
        QB(0x0C, 0x7C); QU32(0x2A); QB(0x7C);                       // a leading [u8][u32] header the scan skips
        QB(0x0A, 0x7C, 0x01, 0x7C, 0x04, 0x7C, 0x00, 0x7C, 0x01, 0x7C); QU32(12345); QB(0x7C); // stage 10 done @12345
        QB(0x14, 0x7C, 0x00, 0x7C, 0x04, 0x7C, 0x00, 0x7C, 0x00, 0x7C);                        // stage 20 not done
        // Objectives block (CHANGE_QUEST_OBJECTIVES): [vsval count=1][7C] then [u32 objIndex=10][7C][u32 status][7C].
        QB(0x04, 0x7C); QU32(10); QB(0x7C); QU32((uint)objStatus); QB(0x7C);

        var questRec = ChangeForm(iref: 1, flags: questFlags, type: 0x49, quest); // 0x49 = formType 9, u16 len

        // Target reference change form (formType 1, u16 length): CHANGE_FORM_FLAGS set, with the new base
        // flags clearing the 0x800 Initially-Disabled bit -> the ref is enabled, so its objective is active.
        var target = new List<byte>();
        target.AddRange([0x0B, 0x00, 0x80, 0x00, 0x7C]); // u32 LE 0x0080000B
        var targetRec = ChangeForm(iref: 2, flags: 0x00000001, type: 0x41, target); // 0x41 = formType 1, u16 len

        // Optional formType-7 quest change form (type 0x07, 1-byte length): the §6 #16 save-anchored seed reads only
        // its presence + the bit30 SCRIPT change-flag, so a tiny payload suffices.
        var ft7Rec = ft7FormId is null
            ? new List<byte>()
            : ChangeForm(iref: 3, flags: ft7Flags, type: 0x07, [0x10, 0x7C, 0x00]);

        var globalData3Offset = changeFormsOffset + questRec.Count + targetRec.Count + ft7Rec.Count;

        // FLT (8 u32) — must sit exactly at bodyStart.
        U32((uint)formIdArrayOffset);  // [0] FormIdArrayCountOffset
        U32((uint)globalData3Offset);  // [1] UnknownTable3Offset
        U32((uint)formIdArrayOffset);  // [2] GlobalData1Offset (unused here)
        U32((uint)changeFormsOffset);  // [3] ChangeFormsOffset
        U32((uint)globalData3Offset);  // [4] GlobalData3Offset (= end of change forms)
        U32(0);                        // [5] GlobalData1Count
        U32(0);                        // [6] GlobalData3Count
        U32((uint)(ft7FormId is null ? 2 : 3)); // [7] ChangeFormCount

        U32((uint)formIds.Length);     // FormID array: count
        foreach (var f in formIds) U32(f);

        b.AddRange(questRec);
        b.AddRange(targetRec);
        b.AddRange(ft7Rec);

        var bytes = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return bytes;
    }

    /// <summary>One change-form record: [refID:3 BE][changeFlags:u32 LE][type:u8][version][len][data]. The length
    /// field width follows the type's top 2 bits (00→1, 01→2, ≥10→4 bytes), matching the engine layout.</summary>
    private static List<byte> ChangeForm(int iref, uint flags, byte type, List<byte> data)
    {
        var rec = new List<byte> { (byte)(iref >> 16), (byte)(iref >> 8), (byte)iref };
        var f = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(f, flags); rec.AddRange(f);
        rec.Add(type);
        rec.Add(0x1B); // version
        switch (type >> 6)
        {
            case 0: rec.Add((byte)data.Count); break;
            case 1: var u16 = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)data.Count); rec.AddRange(u16); break;
            default: var u32 = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)data.Count); rec.AddRange(u32); break;
        }
        rec.AddRange(data);
        return rec;
    }
}
