namespace FnvSaveExplorer.Core;

/// <summary>One Pip-Boy misc stat counter: a value addressed by its index within the record.</summary>
public sealed class MiscStat
{
    public required int Index { get; init; }
    public required uint Value { get; init; }

    /// <summary>Absolute file offset of this stat's 4-byte value, for in-place (same-length) editing.</summary>
    public required int ValueOffset { get; init; }
}

/// <summary>
/// The Misc Stats global-data record (type 0). Layout (verified, exactly fills the record):
/// <c>uint32 count, 0x7C, then count × (uint32 value, 0x7C)</c>. The stats are positional — the game
/// maps each index to a name internally (quests completed, locations discovered, etc.); only the
/// values are stored here.
/// </summary>
public sealed class MiscStatsBlock
{
    public IReadOnlyList<MiscStat> Stats { get; }

    private MiscStatsBlock(IReadOnlyList<MiscStat> stats) => Stats = stats;

    /// <param name="data">The global-data record payload (excluding the type/length header).</param>
    /// <param name="dataFileOffset">Absolute file offset of <paramref name="data"/>, used to address values for editing.</param>
    public static MiscStatsBlock Parse(byte[] data, int dataFileOffset)
    {
        var r = new ByteReader(data);
        uint count = r.ReadUInt32();
        r.Expect(0x7C, "misc stats count terminator");

        var stats = new List<MiscStat>((int)count);
        for (var i = 0; i < count; i++)
        {
            var valueOffset = dataFileOffset + r.Position;
            uint value = r.ReadUInt32();
            r.Expect(0x7C, $"misc stat[{i}] terminator");
            stats.Add(new MiscStat { Index = i, Value = value, ValueOffset = valueOffset });
        }
        return new MiscStatsBlock(stats);
    }
}
