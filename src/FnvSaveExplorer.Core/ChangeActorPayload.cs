using System.Text;

namespace FnvSaveExplorer.Core;

/// <summary>
/// Decoder for the player's <b>CHANGE_ACTOR</b> change-form payload — the change form whose type byte's low
/// six bits are <c>0x0A</c> and whose iref is <c>playerBase (FormID 0x07) + 1</c> (ROADMAP §6 #1 / SPEC §4n).
/// This record holds the actor's <b>added-spell/effect list</b> (the home of <b>addictions</b>), the player's
/// <b>SPECIAL</b>, the player <b>name</b>, and — on records that carry it — a trailing actor/AI package-data blob.
///
/// <para><b>Sections are changeFlags-gated, in this fixed order</b> (pinned by corpus alignment + the
/// <c>beadley-addiction-*</c> controlled FIFO diff across Beadley / Nathan / Mace Windu):</para>
/// <list type="number">
/// <item><b>MOVE block</b> — present iff <see cref="ReferenceChangeForm.ChangeRefrMove"/> (bit 1): a fixed
/// <see cref="MoveBlockLength"/>-byte block + a <c>0x7C</c> delimiter (25 bytes). It is the <em>only</em>
/// section that can precede the spell list — bits 2/4/5 are set on every player record yet add no bytes here,
/// and bit 11 (package data) comes after the name. So Nathan (flags <c>0x834</c>, no bit 1) starts the spell
/// list at offset 0, while Beadley/MW (bit 1 set) start it at 25.</item>
/// <item><b>Added-spell list</b> — always: <c>[count×4 : u8][7C]</c> then <c>(count/4) × [spellRef:3 BE][7C]</c>
/// then a <c>[00][7C]</c> trailer (the same <c>count×4</c> convention as the perk list, §4n). Each
/// <c>spellRef</c> is the FormID-array index + 1 (the §4g "+1" convention), so the spell is
/// <c>FormIdArray[spellRef − 1]</c> → a <c>SPEL</c> record. <b>Addictions</b> are the entries whose <c>SPEL</c>
/// is spell-type <c>Addiction</c> (FNV SPIT type 10 — e.g. "Buffout Withdrawal"); other entries are traits
/// ("Built to Destroy", an Ability) or mod abilities ("Skilled Bonus", OWB). Newest entry first; adding an
/// addiction appends a FormID-array entry and prepends its refID, FIFO removal drops the oldest.</item>
/// <item><b>SPECIAL</b> — always: 7 bytes (each 1..10, summing to the chargen budget) + a <c>0x7C</c>.</item>
/// <item><b>Name</b> — always: <c>[u16 len][7C][len bytes][7C]</c> (matches the save-header player name).</item>
/// <item><b>Package data</b> — present iff <see cref="ReferenceChangeForm.ActorPackageData"/> (bit 11): the
/// remaining bytes, a float-heavy actor/AI-package blob. Left as an explicit <c>unknown[n]</c> gap (per the
/// repo's "size, don't guess" rule) — the reader doesn't need it (it sits after the name).</item>
/// </list>
///
/// <para>The walk consumes the payload <b>exactly</b>: sections 1–4 parse on their delimiters, and the package
/// blob (when bit 11 is set) accounts for the remainder — so a bit-11-clear record ends precisely at the name
/// (zero remainder). That full-length invariant is the structural acceptance test (the §4f "lands exactly"
/// analogue). Pure (bytes in → struct/lines out) so it is unit-testable on synthetic payloads.</para>
/// </summary>
public static class ChangeActorPayload
{
    private const byte Delimiter = 0x7C;

    /// <summary>The form-type (low six bits of the type byte) of a CHANGE_ACTOR change form.</summary>
    public const int FormType = 0x0A;

    /// <summary>Byte length of the leading MOVE block (gated by <see cref="ReferenceChangeForm.ChangeRefrMove"/>),
    /// before its single trailing <c>0x7C</c> delimiter — corpus-pinned at 24 on the player record (Beadley/MW).</summary>
    public const int MoveBlockLength = 24;

    /// <summary>One entry of the actor's added-spell list: the raw 3-byte refID as it appears in the record, and
    /// its data-relative offset. Resolve to a FormID with <c>FormIdArray[RefId − 1]</c> (§4g "+1" convention).</summary>
    public readonly record struct SpellEntry(int RefId, int Offset);

    /// <summary>
    /// The decoded CHANGE_ACTOR record. Offsets are <b>data-relative</b> (add the record's <c>DataOffset</c> for
    /// absolute). <see cref="FullyConsumed"/> is the structural acceptance flag: every byte is accounted for
    /// (sections 1–4 parsed and the trailing package blob, present iff bit 11, covers the rest exactly).
    /// </summary>
    public readonly record struct ActorRecord(
        bool MoveBlockPresent,
        int SpellListOffset,
        IReadOnlyList<SpellEntry> Spells,
        int SpecialOffset,
        byte[] Special,
        int NameOffset,
        string Name,
        int PackageDataOffset,
        int PackageDataLength,
        bool FullyConsumed);

    /// <summary>
    /// Decodes a player CHANGE_ACTOR payload (<paramref name="data"/>) given its record <paramref name="changeFlags"/>.
    /// Returns false when the structure doesn't parse (so the caller declines rather than reads garbage — the
    /// graceful path the SPECIAL/skills/karma locators all take). On success <paramref name="record"/> carries the
    /// decoded sections; check <see cref="ActorRecord.FullyConsumed"/> for the full-length invariant.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> data, uint changeFlags, out ActorRecord record)
    {
        record = default;
        var p = 0;

        // 1. MOVE block (bit 1): 24 fixed bytes + a 0x7C. Required to land exactly when the flag is set.
        var movePresent = (changeFlags & ReferenceChangeForm.ChangeRefrMove) != 0;
        if (movePresent)
        {
            if (data.Length < MoveBlockLength + 1 || data[MoveBlockLength] != Delimiter)
                return false;
            p = MoveBlockLength + 1;
        }

        // 2. Added-spell list: [count×4][7C] (count/4)×[ref:3 BE][7C] [00][7C].
        var spellListOffset = p;
        if (!TryReadSpellList(data, ref p, out var spells))
            return false;

        // 3. SPECIAL: 7 bytes + 0x7C. Each byte a plausible attribute (1..10) so a misalignment is rejected.
        var specialOffset = p;
        if (p + 8 > data.Length || data[p + 7] != Delimiter)
            return false;
        var special = data.Slice(p, 7).ToArray();
        foreach (var s in special)
            if (s is < 1 or > 10)
                return false;
        p += 8;

        // 4. Name: [u16 len][7C][len bytes][7C].
        var nameOffset = p;
        if (p + 3 > data.Length || data[p + 2] != Delimiter)
            return false;
        int nameLen = data[p] | (data[p + 1] << 8);
        var nameStart = p + 3;
        if (nameStart + nameLen + 1 > data.Length || data[nameStart + nameLen] != Delimiter)
            return false;
        var name = Encoding.ASCII.GetString(data.Slice(nameStart, nameLen));
        p = nameStart + nameLen + 1;

        // 5. Package data (bit 11): the remainder. With the flag clear there must be no remainder (the
        //    full-length invariant); with it set, the rest is the package/AI blob (an unknown[n] gap).
        var packagePresent = (changeFlags & ReferenceChangeForm.ActorPackageData) != 0;
        var packageLength = data.Length - p;
        var fully = packageLength >= 0 && (packagePresent ? packageLength > 0 : packageLength == 0);

        record = new ActorRecord(movePresent, spellListOffset, spells, specialOffset, special,
            nameOffset, name, p, Math.Max(0, packageLength), fully);
        return true;
    }

    /// <summary>
    /// Reads the added-spell list at <paramref name="p"/>: a <c>[count×4][7C]</c> prefix (count a non-negative
    /// multiple of 4), then <c>count/4</c> entries of <c>[ref:3 BE][7C]</c> with a positive refID, then a
    /// <c>[xx][7C]</c> trailer. Advances <paramref name="p"/> past the trailer on success. Frames strictly on the
    /// delimiters so it can't run away on a misalignment; a count-0 list (<c>00 7C 00 7C</c>) is valid.
    /// </summary>
    private static bool TryReadSpellList(ReadOnlySpan<byte> data, ref int p, out IReadOnlyList<SpellEntry> spells)
    {
        spells = [];
        if (p + 2 > data.Length || data[p] % 4 != 0 || data[p + 1] != Delimiter)
            return false;
        var n = data[p] / 4;
        var q = p + 2;
        var list = new List<SpellEntry>(n);
        for (var i = 0; i < n; i++)
        {
            if (q + 4 > data.Length || data[q + 3] != Delimiter)
                return false;
            var refId = (data[q] << 16) | (data[q + 1] << 8) | data[q + 2];
            if (refId <= 0)
                return false;
            list.Add(new SpellEntry(refId, q));
            q += 4;
        }
        // [xx][7C] trailer (the xx is 0 on every observed save; we frame on the delimiter).
        if (q + 2 > data.Length || data[q + 1] != Delimiter)
            return false;
        p = q + 2;
        spells = list;
        return true;
    }

    /// <summary>
    /// Renders the CHANGE_ACTOR payload as the labeled field tree consumed by <c>cfwalk</c> (ROADMAP §6 #1b):
    /// the gated sections as labeled fields, the package blob as one explicit <c>unknown[n]</c> gap, and a
    /// fallback to the raw <c>0x7C</c>-token view when the structure doesn't parse (so coverage is always
    /// visible). When <paramref name="resolveRef"/> is supplied (a refID − 1 → display name), each spell entry
    /// shows its resolved name.
    /// </summary>
    public static IEnumerable<string> Walk(uint changeFlags, byte[] data, Func<int, string?>? resolveRef = null)
    {
        if (!TryDecode(data, changeFlags, out var r))
        {
            yield return "(CHANGE_ACTOR — structure did not parse; raw 0x7C tokens follow, §4n)";
            yield break;
        }

        if (r.MoveBlockPresent)
            yield return $"+0x{0:X3}  MOVE block[{MoveBlockLength}] = {Hex(data, 0, MoveBlockLength)}  (bit1; cell/pos state)";

        var count = r.Spells.Count;
        yield return $"+0x{r.SpellListOffset:X3}  spell list = {count} entr{(count == 1 ? "y" : "ies")} (added actor effects; addictions are SPIT type 10, §4n)";
        foreach (var (refId, off) in r.Spells)
        {
            var name = resolveRef?.Invoke(refId - 1);
            yield return $"+0x{off:X3}    spellRef = {refId:X6}{(name is null ? "" : $" -> {name}")}";
        }

        yield return $"+0x{r.SpecialOffset:X3}  SPECIAL = {string.Join(' ', r.Special)}  (sum {r.Special.Sum(b => b)})";
        yield return $"+0x{r.NameOffset:X3}  name = \"{r.Name}\"";
        if (r.PackageDataLength > 0)
            yield return $"+0x{r.PackageDataOffset:X3}  unknown[{r.PackageDataLength}]  actor/AI package data (bit11, float-heavy; §4n)";
        if (!r.FullyConsumed)
            yield return "(warning: payload not fully consumed — section sizes off)";
    }

    private static string Hex(byte[] b, int off, int len) =>
        string.Join(' ', b.Skip(off).Take(len).Select(x => x.ToString("X2")));
}
