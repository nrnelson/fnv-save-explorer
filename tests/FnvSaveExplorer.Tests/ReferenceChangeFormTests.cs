using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class ReferenceChangeFormTests
{
    [Fact]
    public void InventorySearchStart_skips_the_move_block_when_the_flag_is_set()
    {
        // 27-byte MOVE block + its 0x7C delimiter, then the rest. With CHANGE_REFR_MOVE set and the
        // delimiter where it's expected, the search starts just past the block (27 + 1 delimiter).
        var data = new byte[40];
        data[ReferenceChangeForm.MoveBlockLength] = ReferenceChangeForm.Delimiter; // delimiter at index 27

        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrMove);

        Assert.Equal(0x1000 + ReferenceChangeForm.MoveBlockLength + 1, start);
    }

    [Fact]
    public void InventorySearchStart_skips_the_fixed_havok_array_after_the_move_block()
    {
        // After the 27-byte MOVE block (+ delimiter), an inventory reference carries a fixed run of
        // GatedArraySlotCount [4-byte][0x7C] slots before its ExtraDataList. When that exact structure is
        // present the start lands past it — invariant at 1160 bytes on every real save (ROADMAP §4i).
        var afterMove = ReferenceChangeForm.MoveBlockLength + 1;
        var data = new byte[afterMove + ReferenceChangeForm.GatedArrayBlockLength + 8];
        data[ReferenceChangeForm.MoveBlockLength] = ReferenceChangeForm.Delimiter; // MOVE trailing delimiter
        for (var i = 0; i < ReferenceChangeForm.GatedArraySlotCount; i++)
            data[afterMove + i * ReferenceChangeForm.GatedArraySlotStride + 4] = ReferenceChangeForm.Delimiter;

        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x2000, ReferenceChangeForm.ChangeRefrMove);

        Assert.Equal(0x2000 + afterMove + ReferenceChangeForm.GatedArrayBlockLength, start);
        Assert.Equal(1160, ReferenceChangeForm.GatedArrayBlockLength); // the pinned size, guarded against drift
    }

    [Fact]
    public void InventorySearchStart_skips_only_the_move_block_when_the_fixed_array_is_absent()
    {
        // MOVE present but the fixed 232-slot array isn't there (a short/synthetic record) -> stop just past
        // MOVE rather than mis-skipping 1160 bytes; the forward scan then locates the list from there.
        var data = new byte[40];
        data[ReferenceChangeForm.MoveBlockLength] = ReferenceChangeForm.Delimiter;

        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrMove);

        Assert.Equal(0x1000 + ReferenceChangeForm.MoveBlockLength + 1, start);
    }

    [Fact]
    public void InventorySearchStart_does_not_skip_when_the_move_flag_is_clear()
    {
        var data = new byte[40];
        data[ReferenceChangeForm.MoveBlockLength] = ReferenceChangeForm.Delimiter;

        // INVENTORY set but MOVE clear -> no 27-byte block leads the data, so start at the data offset.
        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrInventory);

        Assert.Equal(0x1000, start);
    }

    [Fact]
    public void InventorySearchStart_falls_back_when_the_move_delimiter_is_missing()
    {
        // MOVE flag set but no delimiter at index 27 (a 0x7C fell inside a float, say) -> fall back to the
        // data offset rather than mis-skipping; the forward scan still locates the list.
        var data = new byte[40]; // index 27 is 0x00, not the delimiter

        var start = ReferenceChangeForm.InventorySearchStart(data, dataOffset: 0x1000, ReferenceChangeForm.ChangeRefrMove);

        Assert.Equal(0x1000, start);
    }

    [Fact]
    public void ReadVsval_decodes_the_low_two_bits_as_the_byte_width()
    {
        // vsval: low 2 bits of the first byte are the width (0->1, 1->2, 2->4 bytes); value = field >> 2.
        Assert.Equal(36, ReferenceChangeForm.ReadVsval([0x90], 0, out var w1)); // 0x90: width 0 -> 1 byte
        Assert.Equal(1, w1);
        Assert.Equal(96, ReferenceChangeForm.ReadVsval([0x81, 0x01], 0, out var w2)); // 0x0181: width 1 -> 2 bytes
        Assert.Equal(2, w2);
        Assert.Equal(-1, ReferenceChangeForm.ReadVsval([0x81], 0, out _)); // runs past the buffer
    }

    [Fact]
    public void TryInventoryItemsStart_sizes_the_extra_data_list_and_reads_the_vsval_count()
    {
        // A minimal but well-formed ExtraDataList: header [00 7C f32 7C], a 0x5E ref-list of N=1, the fixed
        // 24-byte 0x18 block, a 0x74 linked-ref entry, then the inventory count vsval (2) before the items.
        var d = new List<byte>();
        d.AddRange([0x00, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]);          // header (7)
        d.AddRange([0x0C, 0x7C, 0x5E, 0x7C, 0x04, 0x7C]);                // ref-list framing, N*4 = 4 -> N=1
        d.AddRange([0x00, 0x00, 0x09, 0x7C, 0x01, 0x7C]);                // one (ref:3 7C flag:1 7C)
        d.Add(0x18); d.AddRange(Enumerable.Repeat((byte)0x00, 23));      // fixed 24-byte block (type + 23)
        d.AddRange([0x74, 0x7C, 0x00, 0x01, 0xD5, 0x7C]);                // linked-ref entry (6)
        d.AddRange([0x08, 0x7C]);                                        // vsval 0x08 -> width 1, value 2
        var expectedItems = d.Count;
        d.AddRange(Enumerable.Repeat((byte)0xAB, 8));                    // (stand-in for the item stacks)

        Assert.True(ReferenceChangeForm.TryInventoryItemsStart(d.ToArray(), 0, out var itemsOffset, out var stackCount));
        Assert.Equal(expectedItems, itemsOffset);
        Assert.Equal(2, stackCount);
    }

    [Fact]
    public void TryInventoryItemsStart_rejects_an_unrecognised_extra_data_list()
    {
        // Header present but no 0x5E ref-list where it's expected -> fail, so the caller forward-scans instead.
        var d = new byte[40];
        d[1] = ReferenceChangeForm.Delimiter;
        d[6] = ReferenceChangeForm.Delimiter;
        Assert.False(ReferenceChangeForm.TryInventoryItemsStart(d, 0, out _, out _));
    }

    [Fact]
    public void DescribeFlags_labels_the_confirmed_reference_bits()
    {
        var text = ReferenceChangeForm.DescribeFlags(
            ReferenceChangeForm.ChangeRefrMove | ReferenceChangeForm.ChangeRefrInventory);

        Assert.Contains("MOVE", text);
        Assert.Contains("INVENTORY", text);
        Assert.Equal("MOVE", ReferenceChangeForm.FlagBitLabels[1]);
        Assert.Equal("INVENTORY", ReferenceChangeForm.FlagBitLabels[5]);
    }
}
