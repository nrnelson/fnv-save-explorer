namespace FnvSaveExplorer.Core;

/// <summary>
/// One stored actor-value modification inside the player's PlayerRef (ACHR) change form. Each is a
/// 7-byte entry <c>[avIndex:u8][0x7C][value:float32][0x7C]</c>; the whole block is length-prefixed
/// with <c>[count*4:u8][0x7C]</c>. <see cref="ValueOffset"/> is the absolute file offset of the
/// 4-byte little-endian float (the editable field; editing it is a safe same-length splice).
/// </summary>
public sealed record ActorValueMod(byte Index, float Value, int ValueOffset);

/// <summary>A named skill resolved from an <see cref="ActorValueMod"/> whose index is a known skill.</summary>
public sealed record SkillValue(string Name, byte Index, float Value, int ValueOffset);

/// <summary>
/// The player's stored skill data, decoded from the actor-value modification block in the PlayerRef
/// (ACHR) change form.
///
/// <para><b>Important — storage is sparse.</b> The engine computes a skill from base + SPECIAL + perks
/// + tag skills and only writes an actor-value <i>modification</i> entry when a skill deviates from
/// that computed base (e.g. a console <c>setav</c>, an implant, certain effects). So a freshly-created
/// character may have no entries at all, and a typical save stores only a handful. This type exposes
/// exactly what is stored — it cannot enumerate all 13 skills for a character that never modified them,
/// and adding a missing skill would be a length-changing edit (unsupported). Editing an entry that
/// <i>is</i> present is a safe same-length float splice.</para>
/// </summary>
public sealed class PlayerSkills
{
    /// <summary>FNV skill actor-value indices → display names. (0x21 "Big Guns" is a Fallout 3 leftover,
    /// unused in New Vegas, which is why the index run skips it.)</summary>
    public static readonly IReadOnlyDictionary<byte, string> SkillNames = new Dictionary<byte, string>
    {
        [0x20] = "Barter",
        [0x22] = "Energy Weapons",
        [0x23] = "Explosives",
        [0x24] = "Lockpick",
        [0x25] = "Medicine",
        [0x26] = "Melee Weapons",
        [0x27] = "Repair",
        [0x28] = "Science",
        [0x29] = "Guns",
        [0x2A] = "Sneak",
        [0x2B] = "Speech",
        [0x2C] = "Survival",
        [0x2D] = "Unarmed",
    };

    /// <summary>Resolves a skill name (case-insensitive, spaces optional, e.g. "EnergyWeapons") to its
    /// actor-value index, or null if it isn't a known skill.</summary>
    public static byte? IndexForSkill(string name)
    {
        var normalized = name.Replace(" ", "").Trim();
        foreach (var (idx, display) in SkillNames)
            if (string.Equals(display.Replace(" ", ""), normalized, StringComparison.OrdinalIgnoreCase))
                return idx;
        return null;
    }

    /// <summary>Absolute file offset of the first entry in the actor-value block.</summary>
    public int Offset { get; }

    /// <summary>Every actor-value modification in the block, in file order (skills plus any non-skill AVs).</summary>
    public IReadOnlyList<ActorValueMod> Modifications { get; }

    public PlayerSkills(int offset, IReadOnlyList<ActorValueMod> modifications)
    {
        Offset = offset;
        Modifications = modifications;
    }

    /// <summary>The subset of <see cref="Modifications"/> that are recognised skills, with display names.</summary>
    public IReadOnlyList<SkillValue> Skills =>
        Modifications.Where(m => SkillNames.ContainsKey(m.Index))
                     .Select(m => new SkillValue(SkillNames[m.Index], m.Index, m.Value, m.ValueOffset))
                     .ToList();

    public override string ToString() =>
        string.Join(", ", Skills.Select(s => $"{s.Name}={s.Value:0.##}"));
}
