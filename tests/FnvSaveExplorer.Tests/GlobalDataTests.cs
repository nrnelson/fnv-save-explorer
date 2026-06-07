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
}
