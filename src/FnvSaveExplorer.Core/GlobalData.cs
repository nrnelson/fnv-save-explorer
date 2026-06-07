namespace FnvSaveExplorer.Core;

/// <summary>
/// One record in a Global Data table: <c>[type:u32][length:u32][data:length]</c>. In New Vegas's
/// global data table 1 the types run 0–11, the most useful being:
/// <list type="bullet">
///   <item>0 — Misc Stats (the Pip-Boy stat counters; see <see cref="MiscStats"/>)</item>
///   <item>1 — Player Location</item>
///   <item>2 — TES (global game state)</item>
///   <item>3 — Global Variables</item>
///   <item>6 — Weather</item>
/// </list>
/// </summary>
public sealed class GlobalData
{
    /// <summary>Numeric type tag identifying the kind of data.</summary>
    public required uint Type { get; init; }

    /// <summary>Payload bytes (length as stored in the record header).</summary>
    public required byte[] Data { get; init; }

    /// <summary>Absolute file offset of the record header (the type field).</summary>
    public required int Offset { get; init; }

    /// <summary>Absolute file offset of the payload (<see cref="Offset"/> + 8).</summary>
    public int DataOffset => Offset + 8;
}
