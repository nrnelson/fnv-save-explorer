using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PlayerInventoryTests
{
    [Fact]
    public void Synthetic_save_locates_and_decodes_inventory()
    {
        var save = FalloutSave.Parse(InventorySave.Build());

        Assert.NotNull(save.Inventory);
        Assert.Equal(3, save.Inventory!.Items.Count);
        var byForm = save.Inventory.Items.ToDictionary(i => i.FormId, i => i.Count);
        Assert.Equal(5u, byForm[0x00AAAA01]);
        Assert.Equal(9u, byForm[0x00AAAA02]);
        Assert.Equal(1u, byForm[0x00AAAA03]);
        Assert.Equal(15, save.Inventory.TotalItems);
    }

    [Fact]
    public void Synthetic_count_edit_is_same_length_and_reparses()
    {
        var original = InventorySave.Build();
        var save = FalloutSave.Parse(original);

        Assert.True(save.TrySetItemCount(0x00AAAA02, 99));
        var edited = save.ToBytes();

        Assert.Equal(original.Length, edited.Length); // nothing shifted
        Assert.Equal(99u, FalloutSave.Parse(edited).Inventory!.Items.Single(i => i.FormId == 0x00AAAA02).Count);
    }

    [Fact]
    public void TrySetItemCount_rejects_absent_item()
    {
        var save = FalloutSave.Parse(InventorySave.Build());

        Assert.False(save.TrySetItemCount(0x00DEAD00, 1)); // not in this inventory
        Assert.False(save.HasPendingEdits);
    }

    [Fact]
    public void Inventory_save_round_trips_byte_identical_with_no_edits()
    {
        var original = InventorySave.Build();
        Assert.Equal(original, FalloutSave.Parse(original).ToBytes());
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_decode_and_safely_edit_inventory(string path)
    {
        var save = FalloutSave.Load(path);
        if (save.Inventory is not { } inv)
            return; // a save whose inventory record couldn't be located — nothing to assert

        Assert.True(inv.Items.Count >= 3);
        Assert.All(inv.Items, i =>
        {
            Assert.InRange(i.CountValueOffset, save.BodyOffset, save.FileLength - 4);
            Assert.NotEqual(0, i.Iref);
            Assert.NotEqual(0u, i.FormId);
        });

        // A same-length count edit of an existing stack must not shift the file and must re-parse.
        var first = inv.Items[0];
        Assert.True(save.TrySetItemCount(first.FormId, 123));
        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length);
        Assert.Equal(123u, FalloutSave.Parse(edited).Inventory!.Items.First(i => i.FormId == first.FormId).Count);
    }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose body carries a File Location Table, a FormID array, and a
/// single change form (the player's inventory record at iref = PlayerRef iref + 1). Its data is a short
/// zeroed preamble followed by inventory stacks <c>[itemIref:3 BE][7C][count:u32 LE][7C][extra:00][7C]</c>,
/// letting the inventory locator/decoder/editor be exercised deterministically without a real save.
/// </summary>
internal static class InventorySave
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

        // ---- Body: File Location Table, FormID array, then the inventory change form ----
        var bodyStart = b.Count;
        var formIdArrayOffset = bodyStart + 32;             // right after the 8-u32 FLT
        uint[] formIds =
        [
            0x00000007, 0x00AAAA01, 0x00AAAA02, 0x00AAAA03, 0x00000FFF,
            0x00000014,           // iref 5 -> PlayerRef (0x14)
            0x05ABCDEF,           // iref 6 -> the inventory record's own form (= playerRef iref + 1)
            0x00BBBB07, 0x00BBBB08, 0x00BBBB09,
        ];
        var changeFormsOffset = formIdArrayOffset + 4 + formIds.Length * 4;

        // Inventory change form: [refID:3=6][changeFlags:u32][type:0x40 -> u16 len][version:0x1B][len:u16][data].
        var data = new List<byte>();
        void DIref3(int iref) { data.Add((byte)(iref >> 16)); data.Add((byte)(iref >> 8)); data.Add((byte)iref); }
        void DU32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); data.AddRange(t); }
        void Entry(int iref, uint count) { DIref3(iref); data.Add(0x7C); DU32(count); data.Add(0x7C); data.Add(0x00); data.Add(0x7C); }
        data.AddRange([0x00, 0x00, 0x00, 0x00, 0x7C, 0x00, 0x00, 0x00, 0x00, 0x7C]); // zeroed preamble (skipped by the locator)
        Entry(1, 5);  // 0x00AAAA01 x5
        Entry(2, 9);  // 0x00AAAA02 x9
        Entry(3, 1);  // 0x00AAAA03 x1

        var rec = new List<byte>();
        rec.AddRange([0x00, 0x00, 0x06]);            // refID = iref 6
        rec.AddRange([0x00, 0x00, 0x04, 0x00]);      // changeFlags
        rec.Add(0x40);                               // type: low 6 bits = form 0x00, high 2 bits = 01 -> u16 length
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

        b.AddRange(rec);               // change-forms region (the one inventory record)

        var bytes = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return bytes;
    }
}
