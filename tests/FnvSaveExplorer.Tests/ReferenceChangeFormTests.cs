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

    // A structurally-valid inventory stack [ref:3 BE][7C][count:u32 LE][7C] (the shape LooksLikeStackStart wants).
    static byte[] Stack(int refId, uint count) =>
    [
        (byte)(refId >> 16), (byte)(refId >> 8), (byte)refId, 0x7C,
        (byte)count, (byte)(count >> 8), (byte)(count >> 16), (byte)(count >> 24), 0x7C,
    ];

    [Fact]
    public void TryInventoryItemsStart_sizes_the_extra_data_list_and_reads_the_vsval_count()
    {
        // A minimal but well-formed ExtraDataList: header [00 7C f32 7C], a 0x5E ref-list of N=1, the fixed
        // 24-byte 0x18 block, a 0x74 linked-ref entry, then the inventory count vsval (2) before the items.
        var d = new List<byte>();
        d.AddRange([0x00, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]);          // header (7)
        d.AddRange([0x0C, 0x7C]);                                        // ExtraDataList lead pair [xx 7C]
        d.AddRange([0x5E, 0x7C, 0x04, 0x7C, 0x00, 0x00, 0x09, 0x7C, 0x01, 0x7C]); // 0x5E ref-list, N=1 (10 bytes)
        d.AddRange([0x18, 0x7C]); d.AddRange(Enumerable.Repeat((byte)0x00, 22)); // fixed 24-byte 0x18 block
        d.AddRange([0x74, 0x7C, 0x00, 0x01, 0xD5, 0x7C]);                // 0x74 linked-ref entry (6)
        d.AddRange([0x08, 0x7C]);                                        // vsval 0x08 -> width 1, value 2
        var expectedItems = d.Count;
        d.AddRange(Stack(0x010203, 5));                                  // a real first stack (so the vsval validates)

        Assert.True(ReferenceChangeForm.TryInventoryItemsStart(d.ToArray(), 0, out var itemsOffset, out var stackCount));
        Assert.Equal(expectedItems, itemsOffset);
        Assert.Equal(2, stackCount);
    }

    [Fact]
    public void TryInventoryItemsStart_handles_modded_reordered_and_new_entry_types()
    {
        // Modded VNV Extended reorders to 18,74,5E,... and adds 0x1D (4+4N) / 0x75 (12). The typed-entry walk
        // must accept any order + the new types and still land on the vsval -> first stack (ROADMAP §4i).
        var d = new List<byte>();
        d.AddRange([0x00, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]);          // header
        d.AddRange([0x14, 0x7C]);                                        // lead pair
        d.AddRange([0x18, 0x7C]); d.AddRange(Enumerable.Repeat((byte)0x00, 22)); // 0x18 first (reordered)
        d.AddRange([0x74, 0x7C, 0x00, 0x01, 0xD5, 0x7C]);                // 0x74
        d.AddRange([0x5E, 0x7C, 0x04, 0x7C, 0x00, 0x00, 0x09, 0x7C, 0x01, 0x7C]); // 0x5E, N=1
        d.AddRange([0x75, 0x7C, 0x00, 0x00, 0x0A, 0x7C, 0x00, 0x00, 0x0B, 0x7C, 0x01, 0x7C]); // 0x75 (12 bytes)
        d.AddRange([0x1D, 0x7C, 0x04, 0x7C, 0x00, 0x00, 0x0C, 0x7C]);    // 0x1D, N=1 (4+4 bytes, no flag byte)
        d.AddRange([0x90, 0x7C]);                                        // vsval 0x90 -> width 1, value 36
        var expectedItems = d.Count;
        d.AddRange(Stack(0x000123, 7));

        Assert.True(ReferenceChangeForm.TryInventoryItemsStart(d.ToArray(), 0, out var itemsOffset, out var stackCount));
        Assert.Equal(expectedItems, itemsOffset);
        Assert.Equal(36, stackCount);
    }

    [Fact]
    public void TryInventoryItemsStart_resyncs_past_a_variable_post_entry_tail()
    {
        // After the recognised entries some modded saves carry a variable 0x04/0x14/0x15 ref-list tail before the
        // vsval (ROADMAP §4i gap 2). The walk should resync past it to the self-validating vsval -> first stack.
        var d = new List<byte>();
        d.AddRange([0x00, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]);          // header
        d.AddRange([0x14, 0x7C]);                                        // lead pair
        d.AddRange([0x74, 0x7C, 0x00, 0x01, 0xD5, 0x7C]);                // a recognised entry
        d.AddRange([0x7C, 0x7C, 0x14, 0x7C, 0x00, 0x21, 0x3A, 0x7C, 0x00, 0x2B, 0xD4, 0x7C]); // post-entry tail
        d.AddRange([0x08, 0x7C]);                                        // vsval value 2
        var expectedItems = d.Count;
        d.AddRange(Stack(0x000456, 3));

        Assert.True(ReferenceChangeForm.TryInventoryItemsStart(d.ToArray(), 0, out var itemsOffset, out var stackCount));
        Assert.Equal(expectedItems, itemsOffset);
        Assert.Equal(2, stackCount);
    }

    [Fact]
    public void TryInventoryItemsStart_rejects_an_unrecognised_extra_data_list()
    {
        // Header present but no lead-pair delimiter / typed entry where expected -> fail, so the caller scans.
        var d = new byte[40];
        d[1] = ReferenceChangeForm.Delimiter;
        d[6] = ReferenceChangeForm.Delimiter;
        Assert.False(ReferenceChangeForm.TryInventoryItemsStart(d, 0, out _, out _));
    }

    [Fact]
    public void LooksLikeStackStart_accepts_a_valid_stack_and_rejects_noise()
    {
        Assert.True(ReferenceChangeForm.LooksLikeStackStart(Stack(0x010203, 5), 0));
        Assert.False(ReferenceChangeForm.LooksLikeStackStart(Stack(0, 5), 0));              // zero ref
        Assert.False(ReferenceChangeForm.LooksLikeStackStart(new byte[9], 0));              // no delimiters
        Assert.False(ReferenceChangeForm.LooksLikeStackStart(Stack(0x010203, 0x7C0000), 0)); // 0x7C in the count's upper bytes
    }

    [Fact]
    public void GatedArrayLength_sizes_a_non_232_slot_run_up_to_the_extra_data_list_header()
    {
        // The havok array's slot count is vanilla-specific (232); modded records vary. GatedArrayLength walks the
        // [4-byte][7C] slot run to the ExtraDataList header [00 7C f32 7C], whatever the count.
        const int slots = 200;
        var d = new List<byte>();
        for (var i = 0; i < slots; i++) d.AddRange([0x00, 0x00, 0x00, 0x00, 0x7C]); // 200 zeroed slots
        d.AddRange([0x00, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]);                      // the header (run ends here)

        var len = ReferenceChangeForm.GatedArrayLength(d.ToArray(), 0, out var sized);
        Assert.Equal(slots, sized);
        Assert.Equal(slots * ReferenceChangeForm.GatedArraySlotStride, len);
    }

    [Fact]
    public void FixedPropertyPayload_sizes_the_corpus_pinned_per_stack_types()
    {
        // Three original (0x16/0x21/0x25) + four pinned by corpus alignment over 607 saves (ROADMAP §4i):
        // 0x6E flag (0), 0x24 u16 (2), 0x1C ref (3), 0x30 float (4). 0x0D stays structured/variable -> -1.
        Assert.Equal(0, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraEquipped)); // 0x16
        Assert.Equal(0, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraFlag6E));   // 0x6E
        Assert.Equal(2, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraU16_24));   // 0x24
        Assert.Equal(3, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraWeaponMod)); // 0x21
        Assert.Equal(3, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraRef1C));    // 0x1C
        Assert.Equal(4, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraCondition)); // 0x25
        Assert.Equal(4, ReferenceChangeForm.FixedPropertyPayload(ReferenceChangeForm.ExtraFloat30));  // 0x30
        Assert.Equal(-1, ReferenceChangeForm.FixedPropertyPayload(0x0D)); // 0x0D isn't a single fixed length -> VariablePropertyLength
    }

    [Fact]
    public void VariablePropertyLength_sizes_the_corpus_pinned_0x0D_grammar()
    {
        // 0x0D is [0D][7C][ref:3][7C][n:u8][7C] (n/4)×([u32:4][7C][f64:8][7C]) [00][7C][00][7C], so its total
        // length is 12 + 14·(n/4). Pinned by aligning all 607 saves (recovered lengths were exactly 12, 26,
        // 54, 68, 110 … for 0, 1, 3, 4, 7 pairs). Each pair is [u32:4][7C][f64:8][7C] = 14 bytes.
        static byte[] D0d(int pairs)
        {
            var d = new List<byte> { 0x0D, 0x7C, 0x00, 0x11, 0x22, 0x7C, (byte)(pairs * 4), 0x7C };
            for (var i = 0; i < pairs; i++)
                d.AddRange([0x0A, 0x00, 0x00, 0x00, 0x7C, 0, 0, 0, 0, 0, 0, 0x30, 0x40, 0x7C]); // u32=10, f64=16.0
            d.AddRange([0x00, 0x7C, 0x00, 0x7C]);                                                // two trailing fields
            return d.ToArray();
        }

        Assert.Equal(12, ReferenceChangeForm.VariablePropertyLength(D0d(0), 0));
        Assert.Equal(26, ReferenceChangeForm.VariablePropertyLength(D0d(1), 0));
        Assert.Equal(54, ReferenceChangeForm.VariablePropertyLength(D0d(3), 0));
        Assert.Equal(12 + 14 * 4, ReferenceChangeForm.VariablePropertyLength(D0d(4), 0));
        // Not a 0x0D, or truncated / malformed framing -> -1 (degrades gracefully).
        Assert.Equal(-1, ReferenceChangeForm.VariablePropertyLength(D0d(1).AsSpan(0, 20).ToArray(), 0)); // truncated
        Assert.Equal(-1, ReferenceChangeForm.VariablePropertyLength([0x25, 0x7C, 0, 0, 0, 0], 0));        // wrong type
    }

    [Fact]
    public void TryReadStackExtra_sizes_0x0D_then_recovers_a_following_condition()
    {
        // The real-save win (ROADMAP §4i): when a 0x0D precedes a 0x25 condition in the same block, the old
        // resync over the unsized 0x0D dropped the condition. Sizing 0x0D lets the walk continue and surface it.
        var d = new List<byte>();
        d.AddRange([0x04, 0x7C, 0x08, 0x7C]);                                  // a=04, b=8 -> 2 properties
        d.AddRange([0x0D, 0x7C, 0x00, 0x11, 0x22, 0x7C, 0x00, 0x7C, 0x00, 0x7C, 0x00, 0x7C]); // 0x0D, 0 pairs (12 B)
        d.AddRange([0x25, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]);                // 0x25 condition = 1.0
        var expectedLength = d.Count;                                          // 4 + 12 + 7 = 23

        Assert.True(ReferenceChangeForm.TryReadStackExtra(d.ToArray(), 0, dataOffset: 0x9000, out var extra));
        Assert.True(extra.FullyDecoded);          // no resync — the block sized through the 0x0D
        Assert.Null(extra.UnknownType);
        Assert.Equal(expectedLength, extra.ByteLength);
        Assert.Equal(1.0f, extra.Condition);      // the condition after the 0x0D is recovered
    }

    [Fact]
    public void TryReadStackExtra_walks_a_block_mixing_all_pinned_types_then_a_condition()
    {
        // A per-stack extra block [04][7C][b][7C] of five properties in mixed order — the four newly-sized
        // types (0x6E/0x24/0x1C/0x30) plus a trailing 0x25 condition — must decode fully (no resync) with
        // the exact byte length, and surface the condition that a resync over an unsized type would have lost.
        var d = new List<byte>();
        d.AddRange([0x04, 0x7C, 0x14, 0x7C]);              // a=04, b=20 -> 5 properties
        d.AddRange([0x6E, 0x7C]);                          // 0x6E flag (0-byte)
        d.AddRange([0x24, 0x7C, 0xAA, 0xBB, 0x7C]);        // 0x24 u16 (2-byte)
        d.AddRange([0x1C, 0x7C, 0x11, 0x22, 0x33, 0x7C]);  // 0x1C ref (3-byte)
        d.AddRange([0x30, 0x7C, 0x85, 0xEB, 0x51, 0x3F, 0x7C]); // 0x30 float (4-byte)
        d.AddRange([0x25, 0x7C, 0x00, 0x00, 0x80, 0x3F, 0x7C]); // 0x25 condition = 1.0 (4-byte)
        var expectedLength = d.Count;                      // 31 bytes

        Assert.True(ReferenceChangeForm.TryReadStackExtra(d.ToArray(), 0, dataOffset: 0x5000, out var extra));
        Assert.True(extra.FullyDecoded);
        Assert.Null(extra.UnknownType);
        Assert.Equal(expectedLength, extra.ByteLength);
        Assert.Equal(1.0f, extra.Condition);
        Assert.Equal(0x5000 + expectedLength - 5, extra.ConditionOffset); // the 4-byte float + its 0x7C
    }

    [Fact]
    public void HavokPhysicsEntryLength_recognises_the_corpus_confirmed_58_byte_entry()
    {
        // The bit2/bit10 (CHANGE_REFR_HAVOK_MOVE) pre-list physics entry (ROADMAP §4i / §10), confirmed across
        // all 113 bit2/bit10 records: [pos:3×f32][7C][quat:4×f32][7C][03][7C][vel:3×f32][7C][angvel:3×f32][7C].
        var e = new List<byte>();
        e.AddRange(Enumerable.Repeat((byte)0x11, 12)); e.Add(0x7C); // pos (3 floats)
        e.AddRange(Enumerable.Repeat((byte)0x22, 16)); e.Add(0x7C); // quat (4 floats)
        e.Add(0x03); e.Add(0x7C);                                   // type 0x03
        e.AddRange(Enumerable.Repeat((byte)0x33, 12)); e.Add(0x7C); // vel (3 floats)
        e.AddRange(Enumerable.Repeat((byte)0x44, 12)); e.Add(0x7C); // angvel (3 floats)
        Assert.Equal(58, e.Count);

        Assert.Equal(58, ReferenceChangeForm.HavokPhysicsEntryLength(e.ToArray(), 0));

        // A wrong type byte, a misplaced delimiter, or truncation all reject (returns -1) — no false positives.
        var badType = e.ToArray(); badType[30] = 0x02;
        Assert.Equal(-1, ReferenceChangeForm.HavokPhysicsEntryLength(badType, 0));
        var badDelim = e.ToArray(); badDelim[12] = 0x00;
        Assert.Equal(-1, ReferenceChangeForm.HavokPhysicsEntryLength(badDelim, 0));
        Assert.Equal(-1, ReferenceChangeForm.HavokPhysicsEntryLength(e.Take(40).ToArray(), 0));
    }

    [Fact]
    public void DescribeFlags_labels_the_confirmed_reference_bits()
    {
        var text = ReferenceChangeForm.DescribeFlags(
            ReferenceChangeForm.ChangeRefrMove | ReferenceChangeForm.ChangeRefrInventory);

        Assert.Contains("MOVE", text);
        Assert.Contains("INVENTORY", text);
        Assert.Equal("MOVE", ReferenceChangeForm.FlagBitLabels[1]);
        Assert.Equal("HAVOK_MOVE", ReferenceChangeForm.FlagBitLabels[2]);
        Assert.Equal("INVENTORY", ReferenceChangeForm.FlagBitLabels[5]);
    }

    [Fact]
    public void DescribeFlags_decodes_the_real_player_actor_flags()
    {
        // The player inventory/actor record's flags on real saves (e.g. vanilla Save 12, iref 368): 0xB0400832
        // = bits 1,4,5,11,22,28,29,31. As an ACTOR record these read cleanly in English (ROADMAP §6 #13).
        var text = ReferenceChangeForm.DescribeFlags(0xB0400832u, ReferenceChangeForm.RefKind.Actor);

        Assert.Equal(
            "bit1(MOVE) bit4(SCALE) bit5(INVENTORY) bit11(ACTOR_PACKAGE_DATA) bit22(ACTOR_OVERRIDE_MODIFIERS) "
            + "bit28(ANIMATION) bit29(ENCOUNTER_ZONE) bit31(GAME_ONLY)",
            text);
    }

    [Fact]
    public void LabelForBit_disambiguates_actor_vs_object_context_bits()
    {
        // Bits 10/11/12/17/21/22/23 mean different things on actor vs object references. RefKind picks the
        // right one; Unknown shows both as actor|object so nothing is silently mislabelled.
        Assert.Equal("ACTOR_OVERRIDE_MODIFIERS", ReferenceChangeForm.LabelForBit(22, ReferenceChangeForm.RefKind.Actor));
        Assert.Equal("OBJECT_OPEN_DEFAULT_STATE", ReferenceChangeForm.LabelForBit(22, ReferenceChangeForm.RefKind.Object));
        Assert.Equal("ACTOR_OVERRIDE_MODIFIERS|OBJECT_OPEN_DEFAULT_STATE", ReferenceChangeForm.LabelForBit(22));

        // bit10: actor LifeState vs object item-data; bit21: actor damage-modifiers vs object empty.
        Assert.Equal("ACTOR_LIFESTATE", ReferenceChangeForm.LabelForBit(10, ReferenceChangeForm.RefKind.Actor));
        Assert.Equal("OBJECT_ITEM_DATA", ReferenceChangeForm.LabelForBit(10, ReferenceChangeForm.RefKind.Object));

        // A shared bit is kind-independent; an unnamed bit is null.
        Assert.Equal("INVENTORY", ReferenceChangeForm.LabelForBit(5, ReferenceChangeForm.RefKind.Object));
        Assert.Equal("INVENTORY", ReferenceChangeForm.LabelForBit(5));
        Assert.Null(ReferenceChangeForm.LabelForBit(13));
    }
}
