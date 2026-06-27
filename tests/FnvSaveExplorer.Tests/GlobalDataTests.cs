using System.Buffers.Binary;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class GlobalDataTests
{
    [Fact]
    public void MiscStats_decodes_count_values_and_edit_offsets()
    {
        // Layout: uint32 count, 0x7C, then count x (uint32 value, 0x7C)
        var data = new List<byte>();
        void U32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); data.AddRange(t); }
        U32(3); data.Add(0x7C);
        U32(10); data.Add(0x7C);
        U32(20); data.Add(0x7C);
        U32(30); data.Add(0x7C);

        const int dataFileOffset = 1000;
        var block = MiscStatsBlock.Parse(data.ToArray(), dataFileOffset);

        Assert.Equal(3, block.Stats.Count);
        Assert.Equal([10u, 20u, 30u], block.Stats.Select(s => s.Value));
        // After count(4) + delimiter(1) = 5, the first value sits at dataFileOffset + 5; each entry is 5 bytes.
        Assert.Equal(1005, block.Stats[0].ValueOffset);
        Assert.Equal(1010, block.Stats[1].ValueOffset);
        Assert.Equal(1015, block.Stats[2].ValueOffset);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_expose_globals_and_editable_misc_stats(string path)
    {
        var save = FalloutSave.Load(path);

        Assert.Equal(12u, save.Flt.GlobalData1Count);
        Assert.Equal(12, save.GlobalDataTable1.Count);
        Assert.Contains(save.GlobalDataTable1, g => g.Type == 0); // Misc Stats present

        Assert.NotNull(save.MiscStats);
        Assert.NotEmpty(save.MiscStats!.Stats);

        // A safe (same-length) stat edit must not change file size and must re-parse to the new value.
        Assert.True(save.TrySetMiscStat(0, 12345));
        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length);
        Assert.Equal(12345u, FalloutSave.Parse(edited).MiscStats!.Stats[0].Value);
    }

    [Fact]
    public void StateChangedRefs_decodes_the_gtg_death_registry_layout()
    {
        // Mirrors the controlled-diff gtg-complete type-2 payload: vsval count 6 (0x18) then
        // 6 x ([refID:3] 7C [status u16] 7C). ROADMAP §6 #16 Stage 2.
        var data = new List<byte> { 0x18, 0x7C };                 // vsval 0x18 >> 2 = 6
        int[] refIds = [0x0021BC, 0x0021BD, 0x0021BE, 0x0021BF, 0x0021C0, 0x0021C1];
        foreach (var r in refIds)
        {
            data.AddRange([(byte)(r >> 16), (byte)(r >> 8), (byte)r]); data.Add(0x7C); // refID (big-endian 3 bytes)
            data.AddRange([0x01, 0x00]); data.Add(0x7C);                                // status u16 = 1 (dead)
        }
        data.AddRange([0x05, 0x00, 0x00, 0x00]);                  // fixed tail

        // Resolver maps refId -> a FormID (here: +0x102AAB so 0x0021BC -> 0x00104C67, the first ganger).
        var entries = FalloutSave.DecodeStateChangedRefs([.. data], r => (uint)(r + 0x102AAB));

        Assert.Equal(6, entries.Count);
        Assert.All(entries, e => Assert.Equal(1, e.Status));
        Assert.Equal(0x00104C67u, entries[0].FormId);
        Assert.Equal(0x0021BC, entries[0].RefId);
    }

    [Fact]
    public void StateChangedRefs_empty_registry_decodes_to_nothing()
    {
        // gtg-active shape: vsval count 0 then the fixed tail.
        var entries = FalloutSave.DecodeStateChangedRefs([0x00, 0x7C, 0x05, 0x00, 0x00, 0x00], r => (uint)r);
        Assert.Empty(entries);
    }

    [Fact]
    public void EnabledReferences_detects_a_form_flags_change_clearing_the_disabled_bit()
    {
        // QuestSave's target ref (iref 2 -> 0x0010A050) carries a CHANGE_FORM_FLAGS change form whose new flags
        // (0x0080000B) clear the 0x800 "Initially Disabled" bit -> the ref reads as ENABLED (ROADMAP §6 #16 Stage 2,
        // the activator/world-state completion signal). The quest change form (0x0010A001, no FORM_FLAGS bit) is not.
        var enabled = FalloutSave.Parse(QuestSave.Build()).EnabledReferences();

        Assert.Contains(0x0010A050u, enabled);
        Assert.DoesNotContain(0x0010A001u, enabled);
    }

    [Fact]
    public void GlobalVariables_decode_layout_value_and_edit_offsets()
    {
        // Type 3 layout (§4c): [vsval count][7C] then count x ([refID:3 BE][7C][value:f32 LE][7C]).
        var data = new List<byte> { 0x0C, 0x7C };  // vsval 0x0C >> 2 = 3 variables
        void Entry(int refId, float value)
        {
            data.AddRange([(byte)(refId >> 16), (byte)(refId >> 8), (byte)refId]); data.Add(0x7C);
            var f = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(f, value); data.AddRange(f); data.Add(0x7C);
        }
        Entry(0x000005, 0.4f);
        Entry(0x000006, 0f);
        Entry(0x000007, 100f);

        const int dataFileOffset = 2000;
        var vars = GlobalDataDecoder.DecodeGlobalVariables([.. data], dataFileOffset, r => (uint)(r + 0x04003600), out var consumed);

        Assert.Equal(3, vars.Count);
        Assert.Equal([0.4f, 0f, 100f], vars.Select(v => v.Value));
        Assert.Equal(0x04003605u, vars[0].FormId);          // refId resolved through the supplied resolver
        Assert.Equal(consumed, data.Count);                  // clean byte-accounting — every payload byte consumed
        // First value sits after count(1) + 7C(1) + refId(3) + 7C(1) = 6; each entry is 9 bytes.
        Assert.Equal(dataFileOffset + 6, vars[0].ValueOffset);
        Assert.Equal(dataFileOffset + 15, vars[1].ValueOffset);
        Assert.Equal(dataFileOffset + 24, vars[2].ValueOffset);
    }

    [Fact]
    public void GlobalDataDecoder_Walk_labels_globals_and_marks_undecoded_types_as_gaps()
    {
        var t3 = new List<byte> { 0x04, 0x7C };  // 1 variable
        t3.AddRange([0x00, 0x00, 0x05]); t3.Add(0x7C);
        var f = new byte[4]; BinaryPrimitives.WriteSingleLittleEndian(f, 0.4f); t3.AddRange(f); t3.Add(0x7C);
        var lines = GlobalDataDecoder.Walk(3, [.. t3], _ => 0x04003605, _ => "NVDLC04Act2XP").ToList();
        Assert.Contains(lines, l => l.Contains("count = 1"));
        Assert.Contains(lines, l => l.Contains("NVDLC04Act2XP") && l.Contains("0.4"));

        // An undecoded type (e.g. 7) is one honest unknown[n] gap, never silently skipped.
        var gap = GlobalDataDecoder.Walk(7, [0x00, 0x7C]).ToList();
        Assert.Single(gap);
        Assert.Contains("unknown[2]", gap[0]);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_global_variables_decode_cleanly_and_edit_round_trips(string path)
    {
        var save = FalloutSave.Load(path);
        var g3 = save.GlobalDataTable1.FirstOrDefault(g => g.Type == 3);
        Assert.NotNull(g3);

        // Self-validation: the decoder must consume the WHOLE type-3 payload (no under-reads) on every real save.
        var vars = GlobalDataDecoder.DecodeGlobalVariables(g3!.Data, g3.DataOffset, save.ResolveRefId, out var consumed);
        Assert.Equal(g3.Data.Length, consumed);
        Assert.NotEmpty(vars);

        // A safe (same-length) global-variable edit must not change file size and must re-parse to the new value.
        var target = vars.First(v => v.FormId != 0);
        Assert.True(save.TrySetGlobalVariable(target.FormId, 123.5f));
        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length);
        var now = FalloutSave.Parse(edited).GlobalVariables().First(v => v.FormId == target.FormId);
        Assert.Equal(123.5f, now.Value);
    }

    [Fact]
    public void MiscStatNames_maps_known_indices_and_is_null_out_of_range()
    {
        Assert.Equal(43, MiscStatNames.Count); // FO3/FNV misc-stat array size
        Assert.Equal("Quests Completed", MiscStatNames.Get(0));
        Assert.Equal("Total Things Killed", MiscStatNames.Get(35));
        Assert.Equal("Slots Games Played", MiscStatNames.Get(42));
        Assert.Null(MiscStatNames.Get(-1));
        Assert.Null(MiscStatNames.Get(43));
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_misc_stat_names_align_with_the_corpus(string path)
    {
        var stats = FalloutSave.Load(path).MiscStats!.Stats;

        // Every stored index must have a name (the corpus has exactly 43 counters).
        foreach (var st in stats)
            Assert.NotNull(MiscStatNames.Get(st.Index));

        // The decisive alignment anchor (ROADMAP §6.8): index 35 "Total Things Killed" is the sum of
        // index 2 "People Killed" and index 3 "Creatures Killed" on every save. This pins the ordering.
        uint Stat(int i) => stats.FirstOrDefault(s => s.Index == i)?.Value ?? 0u;
        Assert.Equal(Stat(2) + Stat(3), Stat(35));
    }
}
