namespace FnvSaveExplorer.Core;

/// <summary>
/// The table of absolute file offsets and counts that begins the save body, immediately after the
/// plugin list. New Vegas uses five offset slots followed by three counts (Skyrim-style but with one
/// fewer global-data table). Every field here was verified empirically against real saves:
///
/// <list type="bullet">
///   <item>[2] global data table 1 — 12 records (verified by walking them onto the next section)</item>
///   <item>[3] change forms — the ~1.4 MB region, <see cref="ChangeFormCount"/> records</item>
///   <item>[4] global data table 3 — a single type-1000 record</item>
///   <item>[0] FormID array — prefixed by its count (e.g. 8108)</item>
///   <item>[1] trailing/unknown table near EOF</item>
/// </list>
/// </summary>
public sealed class FileLocationTable
{
    /// <summary>The raw uint32 slots as stored (offsets followed by counts, then reserved zeros).</summary>
    public uint[] Raw { get; }

    public FileLocationTable(uint[] raw) => Raw = raw;

    public uint FormIdArrayCountOffset => Raw[0];
    public uint UnknownTable3Offset => Raw[1];
    public uint GlobalData1Offset => Raw[2];
    public uint ChangeFormsOffset => Raw[3];
    public uint GlobalData3Offset => Raw[4];
    public uint GlobalData1Count => Raw[5];
    public uint GlobalData3Count => Raw[6];
    public uint ChangeFormCount => Raw[7];
}
