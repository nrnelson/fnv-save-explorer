using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>
/// Exercises the length-changing edits built on <see cref="FalloutSave.RebuildWithBodyEdits"/> (ROADMAP §6 #5):
/// <see cref="FalloutSave.AddPerk"/> (grant-perk) on a synthetic save carrying a count-prefixed perk list, and
/// <see cref="FalloutSave.AddInventoryItem"/> (add-item) as a real-save theory over the deterministic inventory
/// path. Both grow an existing change form's payload and rewrite its fixed-width length field, then fix up the
/// FLT offsets/counts so the change-form walker still lands exactly on GlobalData3Offset.
/// </summary>
public class LengthChangingEditTests
{
    // PerkSave FormID-array layout (see PerkSave.Build).
    private const uint PerkInList1 = 0x00044CA2;   // iref 1 -> a perk already in the list
    private const uint PerkInList2 = 0x00031DD8;   // iref 2 -> a perk already in the list
    private const uint PerkInArrayOnly = 0x00135EC5; // iref 4 -> a perk in the array but NOT yet in the list
    private const uint PerkAbsent = 0x0009835B;    // not in the array at all: AddPerk must append it
    private static bool IsPerk(uint f) =>
        f is PerkInList1 or PerkInList2 or PerkInArrayOnly or PerkAbsent;

    [Fact]
    public void Synthetic_save_reads_the_existing_perks()
    {
        var save = FalloutSave.Parse(PerkSave.Build());
        var perks = save.PlayerPerks(IsPerk).Select(p => p.FormId).ToHashSet();
        Assert.Equal([PerkInList1, PerkInList2], perks);
    }

    [Fact]
    public void AddPerk_for_a_perk_already_in_the_array_does_not_grow_it()
    {
        var before = FalloutSave.Parse(PerkSave.Build());
        var arrayBefore = before.FormIdArray.Count;

        var after = before.AddPerk(PerkInArrayOnly, rank: 0, IsPerk);

        Assert.Equal(arrayBefore, after.FormIdArray.Count);                 // already in the array
        AssertWalkerLandsExactly(after, expectedRecords: 1);
        Assert.Contains(after.PlayerPerks(IsPerk), p => p.FormId == PerkInArrayOnly);
        Assert.Equal(3, after.PlayerPerks(IsPerk).Count);
        AssertRoundTrips(after);
    }

    [Fact]
    public void AddPerk_for_a_new_perk_appends_to_the_formid_array()
    {
        var before = FalloutSave.Parse(PerkSave.Build());
        var arrayBefore = before.FormIdArray.Count;

        var after = before.AddPerk(PerkAbsent, rank: 2, IsPerk);

        Assert.Equal(arrayBefore + 1, after.FormIdArray.Count);             // grew by one
        Assert.Equal(PerkAbsent, after.FormIdArray[^1]);                    // appended at the end
        AssertWalkerLandsExactly(after, expectedRecords: 1);
        var added = after.PlayerPerks(IsPerk).Single(p => p.FormId == PerkAbsent);
        Assert.Equal(2, added.Rank);
        // the pre-existing perks must still resolve (irefs unshifted by the array append)
        Assert.Contains(after.PlayerPerks(IsPerk), p => p.FormId == PerkInList1);
        AssertRoundTrips(after);
    }

    [Fact]
    public void AddPerk_throws_when_the_perk_is_already_present()
    {
        var save = FalloutSave.Parse(PerkSave.Build());
        Assert.Throws<InvalidOperationException>(() => save.AddPerk(PerkInList1, 0, IsPerk));
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_add_inventory_item_grows_and_relands(string path)
    {
        var save = FalloutSave.Load(path);
        // Add-item needs the deterministic inventory path (it locates the vsval stack count to bump).
        if (save.Inventory is not { DeterministicStart: true } inv)
            return;
        var stacksBefore = inv.Items.Count;
        var arrayBefore = save.FormIdArray.Count;
        const uint fakeItem = 0x00FEED01;                 // not a real form -> guaranteed absent from the array
        var existed = save.FindIref(fakeItem) >= 0;

        var after = save.AddInventoryItem(fakeItem, count: 1234);

        AssertWalkerLandsExactly(after, expectedRecords: (int)save.Flt.ChangeFormCount); // count unchanged
        Assert.Equal(arrayBefore + (existed ? 0 : 1), after.FormIdArray.Count);
        Assert.NotNull(after.Inventory);
        Assert.True(after.Inventory!.Items.Count >= stacksBefore + 1);     // at least the new stack
        Assert.Contains(after.Inventory.Items, i => i.FormId == fakeItem && i.Count == 1234);
        AssertRoundTrips(after);
    }

    private static void AssertWalkerLandsExactly(FalloutSave save, int expectedRecords)
    {
        var records = save.EnumerateChangeForms().ToList();
        Assert.Equal(expectedRecords, records.Count);
        Assert.Equal((int)save.Flt.ChangeFormCount, records.Count);
        Assert.Equal((int)save.Flt.GlobalData3Offset, records[^1].Next);
    }

    private static void AssertRoundTrips(FalloutSave save)
    {
        var bytes = save.ToBytes();
        Assert.True(bytes.AsSpan().SequenceEqual(FalloutSave.Parse(bytes).ToBytes()),
            "rebuilt save must round-trip byte-identically");
    }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose player reference change form (iref = PlayerRef + 1) carries a
/// count-prefixed perk list <c>[count*4:u8][7C]</c> + <c>2 × ([perkRef:3 BE][7C][rank:u8][7C])</c>. Tuned for the
/// grant-perk / offset-fixup tests (mirrors <c>ReputationSave</c>).
/// </summary>
internal static class PerkSave
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

        // ---- Body: File Location Table, FormID array, then the player-ref change form ----
        var bodyStart = b.Count;
        var formIdArrayOffset = bodyStart + 32;             // right after the 8-u32 FLT
        uint[] formIds =
        [
            0x00000007,           // iref 0
            0x00044CA2,           // iref 1 -> perk in the list (refID 2)
            0x00031DD8,           // iref 2 -> perk in the list (refID 3)
            0x00000014,           // iref 3 -> PlayerRef (so the player-ref record's refID = 4)
            0x00135EC5,           // iref 4 -> perk in the array only (refID 5), not yet in the list
        ];
        var changeFormsOffset = formIdArrayOffset + 4 + formIds.Length * 4;

        // Player-ref change form: refID = PlayerRef iref(3) + 1 = 4; type 0x41 -> formType 0x01, u16 length width.
        // Payload = perk list: [count*4=8][7C] then 2 × [perkRef:3 BE][7C][rank:u8][7C].
        var data = new List<byte>
        {
            8, 0x7C,                          // count*4 = 8 (two entries)
            0x00, 0x00, 0x02, 0x7C, 0x00, 0x7C, // perkRef 2 (-> FormIdArray[1]), rank 0
            0x00, 0x00, 0x03, 0x7C, 0x00, 0x7C, // perkRef 3 (-> FormIdArray[2]), rank 0
        };

        var rec = new List<byte>();
        rec.AddRange([0x00, 0x00, 0x04]);            // refID = 4 (= PlayerRef iref + 1)
        rec.AddRange([0x00, 0x00, 0x00, 0x00]);      // changeFlags (irrelevant to the perk scan)
        rec.Add(0x41);                               // type: formType 0x01, high bits 01 -> u16 length width
        rec.Add(0x1B);                               // version
        var lenBytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(lenBytes, (ushort)data.Count); rec.AddRange(lenBytes);
        rec.AddRange(data);

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

        b.AddRange(rec);               // change-forms region (the one player-ref record)

        var bytes = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return bytes;
    }
}
