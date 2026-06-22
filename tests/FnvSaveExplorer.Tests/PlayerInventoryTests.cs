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
        Assert.Equal(3, save.Inventory!.Items.Count); // the 3 distinct items, not the longer repeated-ref run
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
    public void Synthetic_decodes_stack_condition_and_no_false_condition()
    {
        var save = FalloutSave.Parse(InventorySave.Build());

        // The middle stack carries a 0x25 condition property (75); the other two carry none (00).
        var withCondition = save.Inventory!.Items.Single(i => i.FormId == 0x00AAAA02);
        Assert.Equal(75f, withCondition.Condition);
        Assert.NotNull(withCondition.ConditionValueOffset);
        Assert.Null(save.Inventory.Items.Single(i => i.FormId == 0x00AAAA01).Condition);
    }

    [Fact]
    public void Synthetic_condition_edit_is_same_length_and_reparses()
    {
        var original = InventorySave.Build();
        var save = FalloutSave.Parse(original);

        Assert.True(save.TrySetItemCondition(0x00AAAA02, 100f));
        var edited = save.ToBytes();

        Assert.Equal(original.Length, edited.Length); // a float splice shifts nothing
        Assert.Equal(100f, FalloutSave.Parse(edited).Inventory!.Items.Single(i => i.FormId == 0x00AAAA02).Condition);
        // A stack with no condition extra-data can't be edited.
        Assert.False(FalloutSave.Parse(original).TrySetItemCondition(0x00AAAA01, 100f));
    }

    [Fact]
    public void Inventory_save_round_trips_byte_identical_with_no_edits()
    {
        var original = InventorySave.Build();
        Assert.Equal(original, FalloutSave.Parse(original).ToBytes());
    }

    [Fact]
    public void PluginForModIndex_maps_to_the_load_order()
    {
        var save = FalloutSave.Parse(InventorySave.Build()); // one plugin, "A.esm", at load-order index 0

        Assert.Equal("A.esm", save.PluginForModIndex(0));
        Assert.Null(save.PluginForModIndex(1));    // past the load order
        Assert.Null(save.PluginForModIndex(0xFF)); // runtime-created
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_inventory_start_is_deterministic_past_the_fixed_havok_array(string path)
    {
        // Locks the structural finding (ROADMAP §4i): on every real inventory record the gated preamble is
        // the 27-byte MOVE block + a *fixed* 1160-byte havok/float array, so the deterministic search start
        // lands exactly at (dataOffset + MOVE + 1 + 1160) — no scan needed to clear it. Verified invariant
        // across both characters, fresh→4 h, and whether or not bit22 is set.
        var save = FalloutSave.Load(path);
        var playerRef = save.FindIref(0x14);
        if (playerRef < 0)
            return;
        FalloutSave.ChangeFormHeader? inv = null;
        foreach (var c in save.EnumerateChangeForms())
            if (c.Iref == playerRef + 1) { inv = c; break; }
        if (inv is not { } cf || (cf.ChangeFlags & ReferenceChangeForm.ChangeRefrMove) == 0)
            return; // not located, or no MOVE-gated preamble to measure

        var data = save.ReadAt(cf.DataOffset, cf.DataLength);
        var start = ReferenceChangeForm.InventorySearchStart(data, cf.DataOffset, cf.ChangeFlags);

        var expected = cf.DataOffset + ReferenceChangeForm.MoveBlockLength + 1 + ReferenceChangeForm.GatedArrayBlockLength;
        Assert.Equal(expected, start);
        // And that offset really is the ExtraDataList (its leading 00 7C ... 00 00 80 3F float-1.0 signature),
        // not mid-array: the last array slot's delimiter sits just before it.
        Assert.Equal(ReferenceChangeForm.Delimiter, save.ReadAt(start - 1, 1)[0]);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_locate_the_inventory_deterministically(string path)
    {
        // The whole point of the §4i work: the item-list start is found deterministically (vsval-validated),
        // not by the heuristic §4g scan. Every discovered real save that has an inventory must set
        // DeterministicStart — measured true on all 30 vanilla + 98 base VNV + 479 VNV Extended saves
        // (the modded corpora live outside the test roots; verified there via `edlscan`). This pins the
        // guarantee against regression on the auto-discovered (vanilla) corpus.
        var save = FalloutSave.Load(path);
        if (save.Inventory is not { } inv)
            return;
        Assert.True(inv.DeterministicStart, $"inventory start should be deterministic ({Path.GetFileName(path)})");
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_inventory_start_sizes_to_the_vsval_count_no_scan_needed(string path)
    {
        // The deterministic finish (ROADMAP §4i): the ExtraDataList sizes to the inventory's vsval stack count,
        // so the first item is reached with no forward scan. The vsval is the engine's authoritative count and
        // validates the start: the decoded chain yields it exactly (28/30 saves) or with a couple of extra
        // interspersed non-item over-reads the name filter hides (the other two) — never fewer. That bound
        // (0 <= decoded - vsval) is what makes the deterministic path accepted rather than falling back.
        var save = FalloutSave.Load(path);
        if (save.Inventory is not { } inv)
            return;
        var playerRef = save.FindIref(0x14);
        FalloutSave.ChangeFormHeader? rec = null;
        foreach (var c in save.EnumerateChangeForms())
            if (c.Iref == playerRef + 1) { rec = c; break; }
        if (rec is not { } cf || (cf.ChangeFlags & ReferenceChangeForm.ChangeRefrMove) == 0)
            return;

        var span = save.ReadAt(0, cf.DataOffset + cf.DataLength); // index == absolute file offset
        var start = ReferenceChangeForm.InventorySearchStart(
            save.ReadAt(cf.DataOffset, cf.DataLength), cf.DataOffset, cf.ChangeFlags);

        Assert.True(ReferenceChangeForm.TryInventoryItemsStart(span, start, out _, out var stackCount));
        Assert.True(stackCount > 0 && stackCount <= inv.Items.Count,
            $"vsval count {stackCount} should be in (0, decoded {inv.Items.Count}]");
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_typed_entry_walk_agrees_with_the_fixed_vanilla_parse(string path)
    {
        // The generalised typed-entry ExtraDataList walk (ROADMAP §4i ◑ — the modded-grammar inspection
        // helper, measured fully-explained on 30 vanilla + 366/479 Viva New Vegas Extended saves) must be a
        // faithful SUPERSET of the proven fixed vanilla parse: wherever TryInventoryItemsStart sizes the list,
        // WalkExtraDataList must also fully explain it and agree on the vsval + first item. (The theory only
        // discovers vanilla saves here; the modded corpus lives outside the test roots — see `edlscan`.)
        var save = FalloutSave.Load(path);
        if (save.Inventory is not { FirstStackOffset: { } firstItem } inv)
            return;
        var playerRef = save.FindIref(0x14);
        FalloutSave.ChangeFormHeader? rec = null;
        foreach (var c in save.EnumerateChangeForms())
            if (c.Iref == playerRef + 1) { rec = c; break; }
        if (rec is not { } cf)
            return;

        var span = save.ReadAt(0, cf.DataOffset + cf.DataLength); // index == absolute file offset
        var start = ReferenceChangeForm.InventorySearchStart(
            save.ReadAt(cf.DataOffset, cf.DataLength), cf.DataOffset, cf.ChangeFlags);

        // Only assert agreement where the fixed parse applies (the vanilla deterministic path).
        if (!ReferenceChangeForm.TryInventoryItemsStart(span, start, out var fixedItems, out var fixedCount))
            return;

        var walk = ReferenceChangeForm.WalkExtraDataList(span, start, firstItem);
        Assert.True(walk.FullyExplained, $"typed-entry walk should fully explain a fixed-parse save ({Path.GetFileName(path)})");
        Assert.Equal(fixedItems, firstItem);        // the located first item == the fixed parse's items offset
        Assert.Equal(fixedCount, (int)walk.Vsval);  // and the same vsval stack count
        Assert.True(walk.Vsval <= inv.Items.Count); // the engine count never exceeds the decoded chain (no under-read)
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_have_no_unsized_per_stack_extra_data(string path)
    {
        // With 0x0D decoded (ROADMAP §4i), every per-stack extra-data property type is now sized, so the walk
        // never resyncs: no decoded stack should carry an UnknownExtraType. Pins the corpus result (vanilla
        // over-read 318→0 of the per-0x0D kind; under-read 0) against regression on the auto-discovered saves.
        var save = FalloutSave.Load(path);
        if (save.Inventory is not { } inv)
            return;
        Assert.All(inv.Items, i => Assert.Null(i.UnknownExtraType));
    }

    [Fact]
    public void ResolveRefId_honours_the_2bit_type()
    {
        var save = FalloutSave.Parse(InventorySave.Build()); // FormID array index 2 -> 0x00AAAA02

        // Type 0 = FormID-array index (the change-form header convention) -> straight ResolveIref.
        Assert.Equal(save.ResolveIref(2), save.ResolveRefId(2));
        Assert.Equal(0x00AAAA02u, save.ResolveRefId(2));
        // Type 2 = created form (plugin index 0xFF): 0x80VVVV -> 0xFF00VVVV, no array lookup.
        Assert.Equal(0xFF001313u, save.ResolveRefId(0x801313));
        // Types 1 and 3 never occur in FNV, so they're left unknown (0) rather than resolved on a spec guess.
        Assert.Equal(0u, save.ResolveRefId(0x401313));
        Assert.Equal(0u, save.ResolveRefId(0xC01313));
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_resolve_created_change_form_refids(string path)
    {
        // Type-2 (created, 0xFF) refIDs occur on real change-form headers (corpus scan: vanilla 135, base VNV
        // 26k, ext 186k); before §6 #15 they indexed out of bounds and resolved to FormId 0. Now each header's
        // FormId must follow the refID's 2-bit type — type 2 -> 0xFF000000 | value, type 0 -> the array lookup.
        var save = FalloutSave.Load(path);
        foreach (var cf in save.EnumerateChangeForms())
        {
            switch (ReferenceChangeForm.RefIdType(cf.Iref))
            {
                case 2:
                    Assert.Equal(0xFF000000u | (uint)ReferenceChangeForm.RefIdValue(cf.Iref), cf.FormId);
                    Assert.Equal(0xFFu, cf.FormId >> 24); // surfaces as a created form
                    break;
                case 0:
                    Assert.Equal(save.ResolveIref(cf.Iref), cf.FormId);
                    break;
            }
        }
    }

    [Fact]
    public void Caps_is_null_and_TrySetCaps_fails_when_no_caps_stack()
    {
        // The synthetic inventory carries no 0x0000000F stack, so there are no caps to read or edit.
        var save = FalloutSave.Parse(InventorySave.Build());

        Assert.Null(save.Caps);
        Assert.False(save.TrySetCaps(500));
        Assert.False(save.HasPendingEdits);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_read_and_safely_edit_caps(string path)
    {
        var save = FalloutSave.Load(path);
        // Caps are an ordinary inventory stack (FormID 0x0000000F, §6.4) — present only once the player
        // has any. Skip saves without a caps stack; where present, Caps must equal that stack's count.
        if (save.Inventory?.Items.FirstOrDefault(i => i.FormId == FalloutSave.CapsFormId) is not { } capStack)
            return;

        Assert.Equal(capStack.Count, save.Caps);

        // Editing caps is a same-length count splice: it must not shift the file and must re-parse.
        Assert.True(save.TrySetCaps(99_999));
        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length);
        Assert.Equal(99_999u, FalloutSave.Parse(edited).Caps);
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

        // Any decoded condition is a plausible health value, and a condition edit is a safe same-length splice.
        Assert.All(inv.Items.Where(i => i.Condition is not null),
            i => Assert.InRange(i.Condition!.Value, 0f, 1_000_000f));
        if (inv.Items.FirstOrDefault(i => i.ConditionValueOffset is not null) is { } repairable)
        {
            var clean = FalloutSave.Load(path);
            Assert.True(clean.TrySetItemCondition(repairable.FormId, 100f));
            var repaired = clean.ToBytes();
            Assert.Equal(clean.FileLength, repaired.Length);
            Assert.Equal(100f, FalloutSave.Parse(repaired).Inventory!.Items
                .First(i => i.FormId == repairable.FormId && i.ConditionValueOffset is not null).Condition);
        }
    }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose body carries a File Location Table, a FormID array, and a
/// single change form (the player's inventory record at iref = PlayerRef iref + 1). Its data is the
/// changeFlags-gated preamble (a 27-byte MOVE block + a zeroed array) followed by inventory stacks
/// <c>[itemIref:3 BE][7C][count:u32 LE][7C][extra:00][7C]</c>, letting the inventory locator/decoder/editor
/// be exercised deterministically without a real save.
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
        // A condition-bearing stack: extra data 04 7C 04 7C 25 7C <float LE> 7C (one 0x25 property).
        void EntryCond(int iref, uint count, float condition)
        {
            DIref3(iref); data.Add(0x7C); DU32(count); data.Add(0x7C);
            data.AddRange([0x04, 0x7C, 0x04, 0x7C, 0x25, 0x7C]);
            var f = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(f, condition); data.AddRange(f); data.Add(0x7C);
        }
        // 27-byte MOVE block (cell ref + position + rotation) + its 0x7C delimiter — the changeFlags-gated
        // preamble the deterministic walk skips (CHANGE_REFR_MOVE, set below). Then a short zeroed array.
        data.AddRange(Enumerable.Range(0, 27).Select(i => (byte)(i + 1))); data.Add(0x7C);
        data.AddRange([0x00, 0x00, 0x00, 0x00, 0x7C, 0x00, 0x00, 0x00, 0x00, 0x7C]); // zeroed array (skipped: refs are 0)
        // Each entry references the FormID array by (index + 1) and carries its own count, so to give the
        // forms at array index 1/2/3 the counts 5/9/1 the references are 2/3/4 (verified by a controlled
        // in-game diff on a real save). The middle stack carries a condition (0x25) property — its exact
        // length is what lets the deterministic walk reach the next stack without a scan window.
        Entry(2, 5);            // ref 2 -> FormIdArray[1] = 0x00AAAA01, x5
        EntryCond(3, 9, 75f);   // ref 3 -> FormIdArray[2] = 0x00AAAA02, x9, condition 75
        Entry(4, 1);            // ref 4 -> FormIdArray[3] = 0x00AAAA03, x1
        // Elsewhere in the record, a *longer* run that repeats a single reference (a misaligned read of a
        // record's non-item data, like the float-byte junk chains in real records). The deterministic walk
        // returns the first chain after the MOVE skip with >= 3 *distinct* references — the real list above —
        // and never reaches this run; even if it did, the run's single distinct ref (1 < 3) rejects it.
        data.AddRange(Enumerable.Repeat((byte)0x00, 64));
        for (var i = 0; i < 12; i++) Entry(2, (uint)(50 - i));

        var rec = new List<byte>();
        rec.AddRange([0x00, 0x00, 0x06]);            // refID = iref 6
        rec.AddRange([0x22, 0x00, 0x00, 0x00]);      // changeFlags = CHANGE_REFR_MOVE (0x2) | CHANGE_REFR_INVENTORY (0x20)
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
