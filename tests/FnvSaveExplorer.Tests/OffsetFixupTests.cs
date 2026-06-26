using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>
/// Exercises the length-changing edit core (<see cref="FalloutSave.RebuildWithBodyEdits"/>, ROADMAP §6 #5)
/// and its first consumer <see cref="FalloutSave.AddReputation"/>. The synthetic save carries a FormID
/// array and a change-forms region with one existing type-0x2B reputation record, so the offset-fixup
/// (FLT offsets + counts) and the change-form-walker landing can be proven without a real save.
/// </summary>
public class OffsetFixupTests
{
    // FormID array layout in the synthetic save (see ReputationSave.Build).
    private const uint FactionWithRecord = 0x000FF001;   // iref 1: already has a reputation record (refID 2)
    private const uint FactionInArray = 0x000FF002;      // iref 2: in the array, no record yet (refID 3)
    private const uint FactionNotInArray = 0x000FF003;   // absent: AddReputation must append it

    [Fact]
    public void RebuildWithBodyEdits_no_op_is_byte_identical()
    {
        var original = ReputationSave.Build();
        var rebuilt = FalloutSave.Parse(original).RebuildWithBodyEdits([]);
        Assert.True(original.AsSpan().SequenceEqual(rebuilt), "no-op rebuild must equal the original bytes");
    }

    [Fact]
    public void Synthetic_save_reads_the_existing_reputation()
    {
        var save = FalloutSave.Parse(ReputationSave.Build());
        var rep = Assert.Single(save.Reputations(_ => true));
        Assert.Equal(FactionWithRecord, rep.FactionFormId);
        Assert.Equal(100f, rep.Fame);
        Assert.Equal(0f, rep.Infamy);
    }

    [Fact]
    public void AddReputation_for_faction_already_in_the_array_does_not_grow_it()
    {
        var before = FalloutSave.Parse(ReputationSave.Build());
        var arrayCountBefore = before.FormIdArray.Count;

        var after = before.AddReputation(FactionInArray, fame: 25f, infamy: 50f);

        Assert.Equal(arrayCountBefore, after.FormIdArray.Count);   // no append needed
        AssertWalkerLandsExactly(after, expectedRecords: 2);
        var rep = after.Reputations(_ => true).Single(r => r.FactionFormId == FactionInArray);
        Assert.Equal(25f, rep.Fame);
        Assert.Equal(50f, rep.Infamy);
        AssertRoundTrips(after);
    }

    [Fact]
    public void AddReputation_for_a_new_faction_appends_to_the_formid_array()
    {
        var before = FalloutSave.Parse(ReputationSave.Build());
        var arrayCountBefore = before.FormIdArray.Count;

        var after = before.AddReputation(FactionNotInArray, fame: 12f, infamy: 34f);

        Assert.Equal(arrayCountBefore + 1, after.FormIdArray.Count);          // grew by one
        Assert.Equal(FactionNotInArray, after.FormIdArray[^1]);              // appended at the end
        AssertWalkerLandsExactly(after, expectedRecords: 2);
        var rep = after.Reputations(_ => true).Single(r => r.FactionFormId == FactionNotInArray);
        Assert.Equal(12f, rep.Fame);
        Assert.Equal(34f, rep.Infamy);
        // The pre-existing record must still resolve to the right faction (irefs unshifted by the append).
        Assert.Contains(after.Reputations(_ => true), r => r.FactionFormId == FactionWithRecord && r.Fame == 100f);
        AssertRoundTrips(after);
    }

    [Fact]
    public void AddReputation_throws_when_the_faction_already_has_a_record()
    {
        var save = FalloutSave.Parse(ReputationSave.Build());
        Assert.Throws<InvalidOperationException>(() => save.AddReputation(FactionWithRecord, 1f, 2f));
    }

    /// <summary>The walker must consume exactly ChangeFormCount records and land precisely on GlobalData3Offset.</summary>
    private static void AssertWalkerLandsExactly(FalloutSave save, int expectedRecords)
    {
        var records = save.EnumerateChangeForms().ToList();
        Assert.Equal(expectedRecords, records.Count);
        Assert.Equal((int)save.Flt.ChangeFormCount, records.Count);
        Assert.Equal((int)save.Flt.GlobalData3Offset, records[^1].Next);
    }

    /// <summary>The rebuilt bytes must themselves round-trip (re-parse then ToBytes is identity).</summary>
    private static void AssertRoundTrips(FalloutSave save)
    {
        var bytes = save.ToBytes();
        Assert.True(bytes.AsSpan().SequenceEqual(FalloutSave.Parse(bytes).ToBytes()),
            "rebuilt save must round-trip byte-identically");
    }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose body carries a File Location Table, a FormID array, and a
/// change-forms region holding one type-0x2B reputation record (fame 100 / infamy 0 for
/// <c>0x000FF001</c>). Mirrors <c>InventorySave</c> but tuned for the reputation / offset-fixup tests.
/// </summary>
internal static class ReputationSave
{
    public static byte[] Build()
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

        // ---- Body: File Location Table, FormID array, then the reputation change form ----
        var bodyStart = b.Count;
        var formIdArrayOffset = bodyStart + 32;             // right after the 8-u32 FLT
        uint[] formIds =
        [
            0x00000007,           // iref 0
            0x000FF001,           // iref 1 -> REPU faction with an existing record (refID 2)
            0x000FF002,           // iref 2 -> REPU faction in the array, no record yet (refID 3)
            0x00000014,           // iref 3 -> PlayerRef
        ];
        var changeFormsOffset = formIdArrayOffset + 4 + formIds.Length * 4;

        // One type-0x2B reputation record: refID = iref(1) + 1 = 2, payload [fame:f32][7C][infamy:f32][7C].
        var rec = new List<byte>();
        rec.AddRange([0x00, 0x00, 0x02]);            // refID = 2 (=> FormIdArray[1])
        rec.AddRange([0x02, 0x00, 0x00, 0x00]);      // changeFlags = 0x00000002 (real type-0x2B value)
        rec.Add(0x2B);                               // type: formType 0x2B, u8 length width
        rec.Add(0x1B);                               // version
        rec.Add(10);                                 // payload length
        var fame = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(fame, 100f); rec.AddRange(fame);
        rec.Add(0x7C);
        var infamy = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(infamy, 0f); rec.AddRange(infamy);
        rec.Add(0x7C);

        var globalData3Offset = changeFormsOffset + rec.Count;

        // FLT (8 u32) — must sit exactly at bodyStart.
        U32((uint)formIdArrayOffset);  // [0] FormIdArrayCountOffset
        U32((uint)globalData3Offset);  // [1] UnknownTable3Offset
        U32((uint)formIdArrayOffset);  // [2] GlobalData1Offset (unused here)
        U32((uint)changeFormsOffset);  // [3] ChangeFormsOffset
        U32((uint)globalData3Offset);  // [4] GlobalData3Offset (= end of change forms)
        U32(0);                        // [5] GlobalData1Count
        U32(0);                        // [6] GlobalData3Count
        U32(1);                        // [7] ChangeFormCount

        U32((uint)formIds.Length);     // FormID array: count
        foreach (var f in formIds) U32(f);

        b.AddRange(rec);               // change-forms region (the one reputation record)

        var bytes = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return bytes;
    }
}
