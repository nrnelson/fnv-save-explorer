using System.Buffers.Binary;

namespace FnvSaveExplorer.Core;

/// <summary>One 0x7C-delimited field of a reference change form's data: its absolute file offset and raw
/// bytes (the byte run between two delimiters). <see cref="Length"/> is the run length.</summary>
public readonly record struct RefField(int Offset, byte[] Bytes)
{
    public int Length => Bytes.Length;

    /// <summary>The field read as a little-endian u32, or null if it isn't exactly 4 bytes.</summary>
    public uint? AsUInt32 => Bytes.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(Bytes) : null;

    /// <summary>The field read as a little-endian float, or null if it isn't exactly 4 bytes.</summary>
    public float? AsSingle => Bytes.Length == 4 ? BinaryPrimitives.ReadSingleLittleEndian(Bytes) : null;

    /// <summary>The field read as a 3-byte big-endian refID (FormID-array index), or null if not 3 bytes.</summary>
    public int? AsRefId => Bytes.Length == 3 ? (Bytes[0] << 16) | (Bytes[1] << 8) | Bytes[2] : null;
}

/// <summary>
/// R&amp;D helpers for reverse-engineering reference (REFR/ACHR/container; change-form type byte
/// <c>0x41</c>/<c>0x42</c>) records — the home of the deterministic-inventory work (ROADMAP §6 ★).
///
/// <para>A reference record's data is pervasively <c>0x7C</c> ('|')-delimited and its sub-sections'
/// presence/order is declared by the record's <c>changeFlags</c> bitmask. For now this exposes two
/// inspection aids: a <c>changeFlags</c> bit description and a <c>0x7C</c> field tokenizer. As each
/// sub-section is decoded (via UESP <c>CHANGE_REFR_*</c> + controlled diffs) it graduates from the raw
/// token view into a typed deterministic walk that replaces the heuristic in
/// <see cref="FalloutSave"/>.</para>
/// </summary>
public static class ReferenceChangeForm
{
    public const byte Delimiter = 0x7C;

    // ---- changeFlags bits that gate a reference record's sections (CHANGE_REFR_*) ------------------
    // Only the bits we have confirmed are named; the rest stay null (see FlagBitLabels). MOVE and
    // INVENTORY are confirmed both empirically (the 27-byte MOVE block appears iff CHANGE_REFR_MOVE is
    // set; CHANGE_REFR_INVENTORY is set on every record that carries an item list) and against the
    // documented Gamebryo/TES change-flag values (MOVE = 0x2, INVENTORY = 0x20).

    /// <summary>CHANGE_REFR_MOVE (bit 1): a 27-byte MOVE block (cell ref + position + rotation) leads the data.</summary>
    public const uint ChangeRefrMove = 0x00000002;

    /// <summary>CHANGE_REFR_INVENTORY (bit 5): the reference carries an inventory item list.</summary>
    public const uint ChangeRefrInventory = 0x00000020;

    /// <summary>Byte length of the MOVE block (cell ref 3 + position 3×f32 + rotation 3×f32 = 27),
    /// followed by a single <c>0x7C</c> delimiter. Verified across every player inventory record.</summary>
    public const int MoveBlockLength = 27;

    // ---- The fixed gated array between MOVE and the ExtraDataList ----------------------------------
    // Immediately after the MOVE block, an inventory reference carries a run of fixed-width slots — the
    // reference's zeroed havok/animation float arrays — before its ExtraDataList (which in turn precedes
    // the item list). Each slot is a 4-byte value followed by a single 0x7C delimiter (5 bytes/slot).
    //
    // EMPIRICAL INVARIANT (all 30 real saves: both characters, fresh→4 h): this run is *always* exactly
    // 232 slots / 1160 bytes — the boundary to the ExtraDataList sits at (afterMove + 1160) on every save,
    // whether or not bit22 is set (flags 0xB0400832 vs 0xB0000832 land identically). So the block is a
    // DETERMINISTIC skip, not a scan. We deliberately do not attribute the 1160 to individual changeFlags
    // bits: bits 4 and 11 are set on every inventory record (so neither can be isolated), and bit22 is
    // shown not to change this block — per the repo's "label, don't guess" rule the per-bit decomposition
    // stays open while the block's total size is pinned. See ROADMAP §4i / §10.

    /// <summary>Slots in the fixed havok/float array that follows the MOVE block (see above).</summary>
    public const int GatedArraySlotCount = 232;

    /// <summary>Bytes per gated-array slot: a 4-byte value + its single <c>0x7C</c> delimiter.</summary>
    public const int GatedArraySlotStride = 5;

    /// <summary>Total byte length of the fixed gated array (1160 = 232 × 5).</summary>
    public const int GatedArrayBlockLength = GatedArraySlotCount * GatedArraySlotStride;

    /// <summary>
    /// Splits a record's payload into <c>0x7C</c>-delimited fields. <paramref name="data"/> is the payload
    /// bytes; <paramref name="dataOffset"/> is its absolute file offset (so each field carries its real
    /// offset). NOTE: a <c>0x7C</c> byte can fall inside a packed float/u32 (a MOVE coordinate, or a stack
    /// count of 124), so a field's length is a hint, not a guarantee — this is an inspection aid, not the
    /// final deterministic parse.
    /// </summary>
    public static List<RefField> Tokenize(ReadOnlySpan<byte> data, int dataOffset)
    {
        var fields = new List<RefField>();
        var start = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != Delimiter)
                continue;
            fields.Add(new RefField(dataOffset + start, data[start..i].ToArray()));
            start = i + 1;
        }
        if (start < data.Length)
            fields.Add(new RefField(dataOffset + start, data[start..].ToArray()));
        return fields;
    }

    /// <summary>
    /// changeFlags bit → section label for reference records (null = not yet identified). Only bits we
    /// have confirmed are named — <see cref="ChangeRefrMove"/> (bit 1) and <see cref="ChangeRefrInventory"/>
    /// (bit 5); the remaining set bits observed on real inventory records (4, 11, 22, 28, 29, 31) are not
    /// yet pinned to a section and stay null per the repo's "label, don't guess" convention.
    /// </summary>
    public static readonly string?[] FlagBitLabels = BuildFlagLabels();

    static string?[] BuildFlagLabels()
    {
        var labels = new string?[32];
        labels[1] = "MOVE";      // CHANGE_REFR_MOVE: 27-byte cell+pos+rot block follows
        labels[5] = "INVENTORY"; // CHANGE_REFR_INVENTORY: the reference carries an item list
        return labels;
    }

    /// <summary>
    /// The absolute offset at which to begin scanning for the inventory item list, by skipping the
    /// changeFlags-gated fixed-size preamble that precedes it: the 27-byte MOVE block (cell + position +
    /// rotation, when <see cref="ChangeRefrMove"/> is set) <em>and</em> the fixed
    /// <see cref="GatedArrayBlockLength"/>-byte havok/float array that follows it. Both are deterministic,
    /// so this lands on the reference's ExtraDataList — the one remaining, still-undecoded section right
    /// before the items — which the caller forward-scans to the first genuine stack chain.
    /// <para><paramref name="data"/> is the record payload and <paramref name="dataOffset"/> its absolute
    /// file offset. Each skip is structurally validated and degrades safely:</para>
    /// <list type="bullet">
    /// <item>MOVE flag clear or the block's trailing delimiter absent (a 0x7C fell inside a float
    /// coordinate) → return <paramref name="dataOffset"/> (scan from the record start).</item>
    /// <item>MOVE present but the fixed array isn't exactly <see cref="GatedArraySlotCount"/> delimited
    /// slots (a synthetic or atypical record) → return just past MOVE, never mis-skipping.</item>
    /// </list>
    /// </summary>
    public static int InventorySearchStart(ReadOnlySpan<byte> data, int dataOffset, uint changeFlags)
    {
        // MOVE block (bit1): 27 bytes + a 0x7C delimiter. Without it we can't anchor — scan from the start.
        if ((changeFlags & ChangeRefrMove) == 0 || MoveBlockLength >= data.Length || data[MoveBlockLength] != Delimiter)
            return dataOffset;
        var afterMove = MoveBlockLength + 1;

        // Fixed havok/float array: GatedArraySlotCount delimited 4-byte slots. Skip it only when that exact
        // structure is present (true on every real inventory record) so the result lands on the ExtraDataList;
        // otherwise fall back to just-past-MOVE and let the forward scan handle the rest.
        if (IsDelimitedSlotRun(data, afterMove, GatedArraySlotCount))
            return dataOffset + afterMove + GatedArrayBlockLength;
        return dataOffset + afterMove;
    }

    /// <summary>True when <paramref name="data"/> from <paramref name="start"/> holds <paramref name="slots"/>
    /// consecutive <c>[4-byte value][0x7C]</c> slots — i.e. a delimiter at every <see cref="GatedArraySlotStride"/>th
    /// byte. (A value's own bytes may include <c>0x7C</c>; only the fixed delimiter position is checked.)</summary>
    static bool IsDelimitedSlotRun(ReadOnlySpan<byte> data, int start, int slots)
    {
        if (start < 0 || (long)start + (long)slots * GatedArraySlotStride > data.Length)
            return false;
        for (var i = 0; i < slots; i++)
            if (data[start + i * GatedArraySlotStride + 4] != Delimiter)
                return false;
        return true;
    }

    // ---- The reference ExtraDataList + inventory count (the deterministic item-list start) ----------
    //
    // Between the fixed havok array (above) and the item stacks sit the reference's own ExtraDataList and
    // the inventory's stack count. Decoded by aligning all 30 real saves; each entry is 0x7C-delimited:
    //
    //   [00][7C][scale:f32][7C]                 reference header (a flags byte + a 1.0 scale on every save)
    //   [xx][7C][5E][7C][N*4][7C] N×(ref:3 7C flag:1 7C)   ExtraDataList ref-list (N entries)
    //   [18][7C][ref:3][7C][pos:3×f32][7C][rot:f32][7C]    fixed 24-byte block (ref + position + rotation)
    //   [74][7C][ref:3][7C]                     a linked-ref entry
    //   ([60][7C][u32][7C])                     OPTIONAL — present on large inventories only
    //   [stackCount : vsval][7C]                Bethesda variable-size value: low 2 bits = byte width
    //   item stacks…
    //
    // The stack count is the key: a vsval whose value (>> 2) equals the number of item stacks that follow,
    // so the walk lands on the first item with **no scan** and self-validates against the decoded count.
    // Verified across all 30 saves (e.g. Save 31 → 0x90 → 36 stacks; quicksave → 0x0181 → 96). See ROADMAP §4i.

    const byte RefListType = 0x5E;     // ExtraDataList ref-list entry
    const byte FixedBlockType = 0x18;  // 24-byte ref + position + rotation block
    const int FixedBlockLength = 24;
    const byte LinkedRefType = 0x74;   // a linked-ref entry just before the count
    const byte U32EntryType = 0x60;    // optional u32 entry on large inventories

    /// <summary>The header (flags + scale) preceding the ExtraDataList: <c>[00][7C][f32][7C]</c> = 7 bytes.</summary>
    public const int RefExtraHeaderLength = 7;

    /// <summary>Reads a Bethesda <c>vsval</c> (variable-size value) at <paramref name="p"/>: the low 2 bits of
    /// the first byte give the width (0→1, 1→2, 2→4 bytes) and the value is the little-endian field <c>&gt;&gt; 2</c>.
    /// Outputs <paramref name="byteLength"/>. Returns -1 if the field would run past <paramref name="data"/>.</summary>
    public static long ReadVsval(ReadOnlySpan<byte> data, int p, out int byteLength)
    {
        byteLength = (data.Length > p ? data[p] & 3 : 0) switch { 0 => 1, 1 => 2, _ => 4 };
        if (p < 0 || p + byteLength > data.Length)
            return -1;
        long raw = 0;
        for (var i = 0; i < byteLength; i++)
            raw |= (long)data[p + i] << (8 * i);
        return raw >> 2;
    }

    /// <summary>
    /// Sizes the reference ExtraDataList + inventory count to land deterministically on the first item stack.
    /// <paramref name="data"/> is indexed by absolute file offset and bounded to the record end;
    /// <paramref name="extraDataListStart"/> is <see cref="InventorySearchStart"/> (past MOVE + the havok array).
    /// On success outputs <paramref name="itemsOffset"/> (the first stack) and <paramref name="stackCount"/> (the
    /// decoded vsval count, for the caller to validate by walking exactly that many stacks). Returns false — so
    /// the caller falls back to the forward scan — if the ExtraDataList isn't the recognised shape (the structural
    /// type anchors <see cref="RefListType"/>/<see cref="FixedBlockType"/>/<see cref="LinkedRefType"/> are the guard
    /// against mis-sizing an atypical/modded record).
    /// </summary>
    public static bool TryInventoryItemsStart(ReadOnlySpan<byte> data, int extraDataListStart, out int itemsOffset, out int stackCount)
    {
        itemsOffset = -1;
        stackCount = -1;
        var p = extraDataListStart;

        // 1. reference header: [00][7C][scale:f32][7C]  (lenient on content; the delimiters frame it).
        if (!Within(data, p, RefExtraHeaderLength) || data[p + 1] != Delimiter || data[p + 6] != Delimiter)
            return false;
        p += RefExtraHeaderLength;

        // 2. ExtraDataList ref-list: [xx][7C][5E][7C][N*4][7C] then N × (ref:3 7C flag:1 7C).
        if (!Within(data, p, 6) || data[p + 1] != Delimiter || data[p + 2] != RefListType
            || data[p + 3] != Delimiter || data[p + 5] != Delimiter)
            return false;
        var n = data[p + 4] / 4;
        p += 6 + 6 * n;

        // 3. fixed 24-byte block, type 0x18.
        if (!Within(data, p, FixedBlockLength) || data[p] != FixedBlockType)
            return false;
        p += FixedBlockLength;

        // 4. linked-ref entry: [74][7C][ref:3][7C].
        if (!Within(data, p, 6) || data[p] != LinkedRefType || data[p + 1] != Delimiter || data[p + 5] != Delimiter)
            return false;
        p += 6;

        // 5. optional u32 entry on large inventories: [60][7C][u32][7C]. (A genuine 24-stack count also encodes
        //    as the byte 0x60, but then it isn't followed by this entry's framing — the count validation below,
        //    and the forward-scan fallback, resolve that rare collision.)
        if (Within(data, p, 7) && data[p] == U32EntryType && data[p + 1] == Delimiter && data[p + 6] == Delimiter)
            p += 7;

        // 6. inventory stack count (vsval) then the first item stack.
        var count = ReadVsval(data, p, out var vlen);
        if (count < 0 || !Within(data, p, vlen + 1) || data[p + vlen] != Delimiter)
            return false;
        stackCount = (int)count;
        itemsOffset = p + vlen + 1;
        return true;
    }

    static bool Within(ReadOnlySpan<byte> data, int start, int length) => start >= 0 && (long)start + length <= data.Length;

    /// <summary>Human description of the set bits in a reference record's <c>changeFlags</c> — labelled
    /// where known (<see cref="FlagBitLabels"/>), otherwise <c>bitN</c>.</summary>
    public static string DescribeFlags(uint flags)
    {
        var parts = new List<string>();
        for (var b = 0; b < 32; b++)
            if ((flags & (1u << b)) != 0)
                parts.Add(FlagBitLabels[b] is { } name ? $"bit{b}({name})" : $"bit{b}");
        return parts.Count == 0 ? "(none)" : string.Join(' ', parts);
    }

    // ---- Per-stack extra data (item condition / equip / weapon mods) -------------------------------
    //
    // Each inventory stack's [ref:3][7C][count:u32][7C] is followed by a per-stack extra-data block:
    //
    //   [a:u8][7C]                                 a == 0x00  -> no extra data (block is 2 bytes)
    //   [a=04:u8][7C][b:u8][7C] props…             a == 0x04  -> b/4 typed properties follow
    //   property = [type:u8][7C] [payload][7C]     (the trailing [7C] only when the payload is non-empty)
    //
    // The property type → payload catalog (CONFIRMED by controlled diffs on vanilla saves 31/32/33 —
    // equip a 9mm pistol then repair it: 0x16 appeared on equip; the 0x25 float went 52.5 → 67.5 on
    // repair; b tracked the property count 04→08):
    //   0x25 ExtraCondition  : 4-byte LE float (item health / weapon condition)   — EDITABLE same-length
    //   0x16 ExtraEquipped   : 0-byte flag; its presence means the item is equipped/worn
    //   0x21 ExtraWeaponMod  : 3-byte BE refID of an attached weapon mod (seen on modded VNV weapons)
    //   0x0D / 0x18 / 0x24 / 0x6E … : longer/structured or mod-added; payload length not yet pinned.
    // See ROADMAP §4i.

    public const byte ExtraEquipped = 0x16;
    public const byte ExtraWeaponMod = 0x21;
    public const byte ExtraCondition = 0x25;

    /// <summary>Fixed payload length (bytes) of a per-stack extra-data property <paramref name="type"/>,
    /// or -1 if the type carries a variable/structured payload we can't yet size deterministically.</summary>
    public static int FixedPropertyPayload(byte type) => type switch
    {
        ExtraEquipped => 0,
        ExtraWeaponMod => 3,
        ExtraCondition => 4,
        _ => -1,
    };

    /// <summary>Decoded per-stack extra data plus the block's byte length. <see cref="FullyDecoded"/> is
    /// false when an unknown/variable property type was hit, in which case <see cref="ByteLength"/> only
    /// covers the decoded prefix and the caller must resynchronise to the next item.</summary>
    public readonly record struct StackExtra(
        int ByteLength, float? Condition, int? ConditionOffset, bool Equipped, IReadOnlyList<int> ModRefIds, bool FullyDecoded)
    {
        public static readonly StackExtra None = new(2, null, null, false, [], true);
    }

    /// <summary>
    /// Reads a per-stack extra-data block from <paramref name="data"/> starting at <paramref name="p"/>
    /// (the byte just after a stack's count delimiter). <paramref name="dataOffset"/> is the absolute file
    /// offset of <paramref name="data"/>[0] so the decoded condition's editable offset is absolute.
    /// Returns false only when the block isn't even structurally valid (bad <c>a</c>/<c>b</c> framing or a
    /// missing delimiter); an unknown property type still returns true with <c>FullyDecoded == false</c>.
    /// </summary>
    public static bool TryReadStackExtra(ReadOnlySpan<byte> data, int p, int dataOffset, out StackExtra extra)
    {
        extra = default;
        if (p + 2 > data.Length || data[p + 1] != Delimiter)
            return false;
        var a = data[p];
        if (a == 0)
        {
            extra = StackExtra.None;
            return true;
        }
        if (a != 0x04 || p + 4 > data.Length || data[p + 3] != Delimiter)
            return false;
        var propCount = data[p + 2] / 4;
        var cur = p + 4;
        float? condition = null; int? conditionOffset = null; var equipped = false; List<int>? mods = null;
        var fully = true;
        for (var i = 0; i < propCount; i++)
        {
            if (cur + 2 > data.Length || data[cur + 1] != Delimiter)
            {
                fully = false;
                break;
            }
            var type = data[cur];
            var payloadStart = cur + 2;
            var plen = FixedPropertyPayload(type);
            if (plen < 0)
            {
                fully = false; // structured/unknown — can't size the rest deterministically
                break;
            }
            if (payloadStart + plen > data.Length)
            {
                fully = false;
                break;
            }
            switch (type)
            {
                case ExtraCondition:
                    condition = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(payloadStart, 4));
                    conditionOffset = dataOffset + payloadStart;
                    break;
                case ExtraEquipped:
                    equipped = true;
                    break;
                case ExtraWeaponMod:
                    (mods ??= []).Add((data[payloadStart] << 16) | (data[payloadStart + 1] << 8) | data[payloadStart + 2]);
                    break;
            }
            // type(1) + delimiter(1), then payload + its delimiter only when the payload is non-empty.
            cur = payloadStart + (plen > 0 ? plen + 1 : 0);
        }
        extra = new StackExtra(cur - p, condition, conditionOffset, equipped, (IReadOnlyList<int>?)mods ?? [], fully);
        return true;
    }
}
