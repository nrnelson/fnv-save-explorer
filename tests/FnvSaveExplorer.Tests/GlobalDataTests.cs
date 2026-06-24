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
