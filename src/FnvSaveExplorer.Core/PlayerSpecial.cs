namespace FnvSaveExplorer.Core;

/// <summary>
/// The player's seven SPECIAL attributes, located inside the player base (TESNPC_) change form.
/// They are stored as 7 consecutive bytes immediately before the length-prefixed player-name field
/// (verified across saves: the values always sum to the New Vegas chargen budget of 40 for a fresh
/// character, and match per character). Each is a single byte, so editing is a safe same-length splice.
/// </summary>
public sealed class PlayerSpecial
{
    /// <summary>Absolute file offset of the 7-byte SPECIAL block.</summary>
    public int Offset { get; }

    public byte Strength { get; }
    public byte Perception { get; }
    public byte Endurance { get; }
    public byte Charisma { get; }
    public byte Intelligence { get; }
    public byte Agility { get; }
    public byte Luck { get; }

    public PlayerSpecial(int offset, ReadOnlySpan<byte> values)
    {
        Offset = offset;
        Strength = values[0];
        Perception = values[1];
        Endurance = values[2];
        Charisma = values[3];
        Intelligence = values[4];
        Agility = values[5];
        Luck = values[6];
    }

    public IReadOnlyList<byte> Values =>
        [Strength, Perception, Endurance, Charisma, Intelligence, Agility, Luck];

    public int Sum => Values.Sum(v => v);

    public override string ToString() =>
        $"S{Strength} P{Perception} E{Endurance} C{Charisma} I{Intelligence} A{Agility} L{Luck}";
}
