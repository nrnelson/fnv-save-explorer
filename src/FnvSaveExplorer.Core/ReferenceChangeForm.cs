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

    // ---- changeFlags bits that gate a reference record's sections (CHANGE_REFR_* / CHANGE_ACTOR_* etc.) ----
    // The changeFlags is an engine-level (Gamebryo/Creation) bitmask. We FNV-corpus-CONFIRMED three bits —
    // MOVE (the 27-byte block appears iff bit1), HAVOK_MOVE (the physics blob appears iff bit2, §4i), and
    // INVENTORY (set on every record carrying an item list) — and they match the documented values exactly,
    // which is good evidence the rest of the enum is shared too. The remaining bit NAMES below are
    // cross-referenced from the UESP Skyrim ChangeFlags spec (ROADMAP §8a) and are NOT yet FNV-verified by a
    // controlled diff (form-type *numbering* differs between FNV and Skyrim, so per-bit confirmation is still
    // owed — ROADMAP §6 #13). They're surfaced for readability (refdump/walk) with this provenance noted.
    //
    // IMPORTANT: bits 10/11/12/17/21/22/23 mean DIFFERENT things on ACTOR (ACHR) vs OBJECT (non-actor REFR)
    // records, so DescribeFlags takes a RefKind to disambiguate; with RefKind.Unknown it shows both.

    /// <summary>CHANGE_REFR_MOVE (bit 1): a 27-byte MOVE block (cell ref + position + rotation) leads the data.</summary>
    public const uint ChangeRefrMove = 0x00000002;

    /// <summary>CHANGE_REFR_HAVOK_MOVE (bit 2): an active-Havok-physics blob follows the MOVE block (§4i).</summary>
    public const uint ChangeRefrHavokMove = 0x00000004;

    /// <summary>CHANGE_REFR_SCALE (bit 4): a float scale field is present (spec-derived).</summary>
    public const uint ChangeRefrScale = 0x00000010;

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

    /// <summary>Total byte length of the fixed gated array (1160 = 232 × 5) — the vanilla invariant.</summary>
    public const int GatedArrayBlockLength = GatedArraySlotCount * GatedArraySlotStride;

    // The 232-slot size above is vanilla-specific: late-game MODDED records carry a *different* slot count
    // (~214 observed; correlates with extra changeFlags bits), so the array must be SIZED, not assumed, for the
    // search start to land on the ExtraDataList. GatedArrayLength walks the [4-byte][7C] slot run until the
    // ExtraDataList header begins; the run never approaches this bound on a real save (it is just a runaway guard).
    // See ROADMAP §4i / §10.

    /// <summary>Upper bound on havok-array slots the variable sizer will walk before giving up (runaway guard).</summary>
    public const int GatedArrayMaxSlots = 512;

    // ---- Player karma + XP: two floats in the post-MOVE actor-value array (ROADMAP §4j) ------------
    // The fixed array after the MOVE block isn't all zeroed havok state — specific slots cache the
    // player reference's actor values. A controlled in-game diff (vanilla Saves 33/34/35 for XP, 35/36/37
    // for karma) pinned two adjacent [f32][7C] slots: karma at slot 100, XP (experience points) at slot
    // 101 (0-indexed within the array). Verified the slot indices on a second character (Mace Windu: karma
    // 35, XP 338, both sane). Both are same-length float edits.

    /// <summary>0-indexed slot of the player's karma (a float32) within the post-MOVE actor-value array.</summary>
    public const int PlayerKarmaSlot = 100;

    /// <summary>0-indexed slot of the player's experience points (a float32) — the slot right after karma.</summary>
    public const int PlayerXpSlot = 101;

    /// <summary>
    /// Absolute file offset of the float32 in the player reference's post-MOVE actor-value array at
    /// 0-indexed <paramref name="slot"/> (e.g. <see cref="PlayerKarmaSlot"/> / <see cref="PlayerXpSlot"/>),
    /// or -1 when it can't be located safely. Requires the MOVE block (its trailing delimiter present) and
    /// the full vanilla fixed array (<see cref="GatedArraySlotCount"/> delimited <c>[4-byte][7C]</c> slots),
    /// which guarantees the slot is a real delimited float and excludes the variable-length Havok-physics
    /// records (bit2/bit10, ROADMAP §4i) whose pre-list region isn't a slot array — there the caller
    /// declines gracefully (null karma/XP), matching the SPECIAL/skills locators.
    /// <para><paramref name="data"/> is the record payload, <paramref name="dataOffset"/> its absolute offset,
    /// <paramref name="changeFlags"/> the record's flags.</para>
    /// </summary>
    public static int PlayerStatSlotOffset(ReadOnlySpan<byte> data, int dataOffset, uint changeFlags, int slot)
    {
        if (slot < 0 || (changeFlags & ChangeRefrMove) == 0 || MoveBlockLength >= data.Length || data[MoveBlockLength] != Delimiter)
            return -1;
        var afterMove = MoveBlockLength + 1;
        if (slot >= GatedArraySlotCount || !IsDelimitedSlotRun(data, afterMove, GatedArraySlotCount))
            return -1;
        return dataOffset + afterMove + slot * GatedArraySlotStride;
    }

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

    /// <summary>Which kind of reference a change form is, for disambiguating the context-dependent
    /// <c>changeFlags</c> bits (10/11/12/17/21/22/23). <see cref="Actor"/> = ACHR (the player/NPCs/creatures);
    /// <see cref="Object"/> = a non-actor placed reference (container/door/item/…); <see cref="Unknown"/> when
    /// the record kind isn't known (those bits are then shown with both meanings).</summary>
    public enum RefKind { Unknown, Actor, Object }

    /// <summary>
    /// changeFlags bit → label for the bits whose meaning is the SAME on actor and object references (null =
    /// not identified). MOVE (1), HAVOK_MOVE (2) and INVENTORY (5) are FNV-corpus-confirmed; the rest are
    /// cross-referenced from the UESP Skyrim spec (ROADMAP §8a / §6 #13) and shown for readability. The
    /// kind-dependent bits (10/11/12/17/21/22/23) live in <see cref="ActorFlagBitLabels"/> /
    /// <see cref="ObjectFlagBitLabels"/>, not here.
    /// </summary>
    public static readonly string?[] FlagBitLabels = BuildSharedLabels();

    /// <summary>changeFlags bit → label when the record is an ACTOR (ACHR). Only the actor-specific bits are
    /// populated; for the shared bits see <see cref="FlagBitLabels"/>. Spec-derived (UESP, ROADMAP §8a).</summary>
    public static readonly string?[] ActorFlagBitLabels = BuildActorLabels();

    /// <summary>changeFlags bit → label when the record is a non-actor OBJECT reference. Only the
    /// object-specific bits are populated; for the shared bits see <see cref="FlagBitLabels"/>. Spec-derived.</summary>
    public static readonly string?[] ObjectFlagBitLabels = BuildObjectLabels();

    static string?[] BuildSharedLabels()
    {
        var l = new string?[32];
        l[0] = "FORM_FLAGS";            // CHANGE_FORM_FLAGS
        l[1] = "MOVE";                  // CHANGE_REFR_MOVE          (FNV-confirmed: 27-byte block)
        l[2] = "HAVOK_MOVE";            // CHANGE_REFR_HAVOK_MOVE    (FNV-confirmed: physics blob, §4i)
        l[3] = "CELL_CHANGED";          // CHANGE_REFR_CELL_CHANGED
        l[4] = "SCALE";                 // CHANGE_REFR_SCALE
        l[5] = "INVENTORY";             // CHANGE_REFR_INVENTORY     (FNV-confirmed: item list)
        l[6] = "EXTRA_OWNERSHIP";       // CHANGE_REFR_EXTRA_OWNERSHIP
        l[7] = "BASEOBJECT";            // CHANGE_REFR_BASEOBJECT
        l[25] = "PROMOTED";             // CHANGE_REFR_PROMOTED
        l[26] = "ACTIVATING_CHILDREN";  // CHANGE_REFR_EXTRA_ACTIVATING_CHILDREN
        l[27] = "LEVELED_INVENTORY";    // CHANGE_REFR_LEVELED_INVENTORY
        l[28] = "ANIMATION";            // CHANGE_REFR_ANIMATION
        l[29] = "ENCOUNTER_ZONE";       // CHANGE_REFR_EXTRA_ENCOUNTER_ZONE
        l[30] = "CREATED_ONLY";         // CHANGE_REFR_EXTRA_CREATED_ONLY
        l[31] = "GAME_ONLY";            // CHANGE_REFR_EXTRA_GAME_ONLY
        return l;
    }

    static string?[] BuildActorLabels()
    {
        var l = new string?[32];
        l[10] = "ACTOR_LIFESTATE";              // CHANGE_ACTOR_LIFESTATE
        l[11] = "ACTOR_PACKAGE_DATA";           // CHANGE_ACTOR_EXTRA_PACKAGE_DATA
        l[12] = "ACTOR_MERCHANT_CONTAINER";     // CHANGE_ACTOR_EXTRA_MERCHANT_CONTAINER
        l[17] = "ACTOR_DISMEMBERED_LIMBS";      // CHANGE_ACTOR_EXTRA_DISMEMBERED_LIMBS
        l[18] = "ACTOR_LEVELED";                // CHANGE_ACTOR_LEVELED_ACTOR
        l[19] = "ACTOR_DISPOSITION_MODIFIERS";  // CHANGE_ACTOR_DISPOSITION_MODIFIERS
        l[20] = "ACTOR_TEMP_MODIFIERS";         // CHANGE_ACTOR_TEMP_MODIFIERS
        l[21] = "ACTOR_DAMAGE_MODIFIERS";       // CHANGE_ACTOR_DAMAGE_MODIFIERS
        l[22] = "ACTOR_OVERRIDE_MODIFIERS";     // CHANGE_ACTOR_OVERRIDE_MODIFIERS
        l[23] = "ACTOR_PERMANENT_MODIFIERS";    // CHANGE_ACTOR_PERMANENT_MODIFIERS
        return l;
    }

    static string?[] BuildObjectLabels()
    {
        var l = new string?[32];
        l[10] = "OBJECT_ITEM_DATA";         // CHANGE_OBJECT_EXTRA_ITEM_DATA
        l[11] = "OBJECT_AMMO";              // CHANGE_OBJECT_EXTRA_AMMO
        l[12] = "OBJECT_LOCK";              // CHANGE_OBJECT_EXTRA_LOCK
        l[17] = "DOOR_TELEPORT";            // CHANGE_DOOR_EXTRA_TELEPORT
        l[21] = "OBJECT_EMPTY";             // CHANGE_OBJECT_EMPTY
        l[22] = "OBJECT_OPEN_DEFAULT_STATE"; // CHANGE_OBJECT_OPEN_DEFAULT_STATE
        l[23] = "OBJECT_OPEN_STATE";        // CHANGE_OBJECT_OPEN_STATE
        return l;
    }

    // ---- QUST change-form flags (ROADMAP §6 #10 / §6 #13) -------------------------------------------
    // A QUST change form is NOT a reference, so the REFR/ACHR bit table above does not apply: bit31 is
    // CHANGE_QUEST_STAGES here, not GAME_ONLY. The top three bits are FNV-corroborated by the §6 #10
    // controlled diff (chargen "Ain't That a Kick" 0x80000000→0xC0000000 grew a script field; the active
    // formType-9 forms carry 0xE0000002): bit31 STAGES, bit30 SCRIPT, bit29 OBJECTIVES. bit0 FORM_FLAGS is
    // the shared CHANGE_FORM_FLAGS; bit1 QUEST_FLAGS is spec-derived (UESP) and shown for readability.

    /// <summary>changeFlags bit → label for a <c>QUST</c> change form (distinct from the REFR/ACHR tables —
    /// see <see cref="DescribeQuestFlags"/>). null = not identified.</summary>
    public static readonly string?[] QuestFlagBitLabels = BuildQuestLabels();

    /// <summary>CHANGE_QUEST_STAGES (bit 31): the record carries the quest's stage list / packed stage state.</summary>
    public const uint ChangeQuestStages = 0x80000000;

    /// <summary>CHANGE_QUEST_SCRIPT (bit 30): the record carries quest-script state (a <c>[u32 script]</c> field).</summary>
    public const uint ChangeQuestScript = 0x40000000;

    /// <summary>CHANGE_QUEST_OBJECTIVES (bit 29): the record carries quest-objective state.</summary>
    public const uint ChangeQuestObjectives = 0x20000000;

    static string?[] BuildQuestLabels()
    {
        var l = new string?[32];
        l[0] = "FORM_FLAGS";        // CHANGE_FORM_FLAGS (shared)
        l[1] = "QUEST_FLAGS";       // CHANGE_QUEST_FLAGS (spec-derived)
        l[29] = "QUEST_OBJECTIVES"; // CHANGE_QUEST_OBJECTIVES (§6 #10)
        l[30] = "QUEST_SCRIPT";     // CHANGE_QUEST_SCRIPT     (§6 #10)
        l[31] = "QUEST_STAGES";     // CHANGE_QUEST_STAGES     (§6 #10)
        return l;
    }

    /// <summary>Describes a <c>QUST</c> change form's <c>changeFlags</c> using the quest bit table
    /// (<see cref="QuestFlagBitLabels"/>) — the counterpart to <see cref="DescribeFlags"/> for reference
    /// records, which would mislabel bit31 as <c>GAME_ONLY</c> on a quest (ROADMAP §6 #13).</summary>
    public static string DescribeQuestFlags(uint flags)
    {
        var parts = new List<string>();
        for (var b = 0; b < 32; b++)
            if ((flags & (1u << b)) != 0)
                parts.Add(QuestFlagBitLabels[b] is { } name ? $"bit{b}({name})" : $"bit{b}");
        return parts.Count == 0 ? "(none)" : string.Join(' ', parts);
    }

    /// <summary>The label for one set <c>changeFlags</c> bit given the record <paramref name="kind"/>: the shared
    /// label, or — for a context-dependent bit — the actor/object label (or both, <c>actor|object</c>, when
    /// <paramref name="kind"/> is <see cref="RefKind.Unknown"/>). Returns null when the bit isn't named.</summary>
    public static string? LabelForBit(int bit, RefKind kind = RefKind.Unknown)
    {
        if (bit is < 0 or > 31)
            return null;
        var actor = ActorFlagBitLabels[bit];
        var obj = ObjectFlagBitLabels[bit];
        if (actor is null && obj is null)
            return FlagBitLabels[bit]; // shared (or unnamed)
        return kind switch
        {
            RefKind.Actor => actor ?? obj,
            RefKind.Object => obj ?? actor,
            _ => actor is not null && obj is not null ? $"{actor}|{obj}" : actor ?? obj,
        };
    }

    // ---- RefID 2-bit type (UESP §8a / ROADMAP §6 #15) ----------------------------------------------
    // A 3-byte big-endian refID packs a 2-bit TYPE in the top bits of its first byte and a 22-bit VALUE in
    // the rest. Confirmed by a corpus scan (all three sets): FNV uses only type 0 (a FormID-array index) and
    // type 2 (a *created* form, plugin index 0xFF) — type 1 (base-master formID) and type 3 never appear.
    // Type 2 occurs on change-form HEADERS (created references); inventory item refs + extra-data refs are
    // always type 0. Resolution itself needs the save's FormID array, so it lives in FalloutSave.ResolveRefId.

    /// <summary>The 2-bit type packed in the top of a 3-byte refID: 0 = FormID-array index, 1 = base-master
    /// formID, 2 = created (plugin index 0xFF), 3 = unspecified. FNV uses only 0 and 2.</summary>
    public static int RefIdType(int raw24) => (raw24 >> 22) & 3;

    /// <summary>The lower 22 bits of a 3-byte refID — its index/formID value once the 2-bit type is stripped.</summary>
    public static int RefIdValue(int raw24) => raw24 & 0x3FFFFF;

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

        // Fixed havok/float array (vanilla fast path): exactly GatedArraySlotCount delimited 4-byte slots. Kept
        // verbatim so every vanilla save lands byte-identically at MOVE+1+1160 (and the pinned-size tests hold).
        if (IsDelimitedSlotRun(data, afterMove, GatedArraySlotCount))
            return dataOffset + afterMove + GatedArrayBlockLength;

        // Modded: the array size is variable (late records carry ~214 slots, not 232 — ROADMAP §4i). Size it by
        // walking the [4-byte][7C] slot run to the ExtraDataList header, so the result still lands on the list.
        var sized = GatedArrayLength(data, afterMove, out _);
        if (sized >= 0)
            return dataOffset + afterMove + sized;

        // Couldn't size it (a synthetic/atypical record) → return just past MOVE; the forward scan handles the rest.
        return dataOffset + afterMove;
    }

    /// <summary>
    /// Sizes the variable-length havok/float array that follows the MOVE block: the run of
    /// <c>[4-byte value][0x7C]</c> slots that ends where the reference's ExtraDataList header
    /// (<see cref="IsExtraDataListHeader"/>) begins. Returns the run's total byte length (slots ×
    /// <see cref="GatedArraySlotStride"/>) and outputs <paramref name="slotCount"/>, or -1 if no header is
    /// reached within <see cref="GatedArrayMaxSlots"/> or the run breaks (a non-slot byte before any header).
    /// Vanilla records size to exactly <see cref="GatedArraySlotCount"/> (232); modded records vary.
    /// <para><paramref name="afterMove"/> is the offset just past the MOVE block's delimiter, relative to
    /// <paramref name="data"/>'s base.</para>
    /// </summary>
    public static int GatedArrayLength(ReadOnlySpan<byte> data, int afterMove, out int slotCount)
    {
        slotCount = -1;
        for (var slots = 0; slots <= GatedArrayMaxSlots; slots++)
        {
            var p = afterMove + slots * GatedArraySlotStride;
            if (IsExtraDataListHeader(data, p)) // the run ends exactly at the ExtraDataList header
            {
                slotCount = slots;
                return slots * GatedArraySlotStride;
            }
            if (p + GatedArraySlotStride > data.Length || data[p + 4] != Delimiter) // not a slot either → can't size
                return -1;
        }
        return -1;
    }

    /// <summary>True when <paramref name="data"/> at <paramref name="p"/> is the reference ExtraDataList header
    /// <c>[00][7C][scale:f32][7C]</c> (7 bytes): a <c>0x00</c> flags byte, delimiters at +1 and +6, and a +4 byte
    /// that is <em>not</em> the delimiter (so a havok slot <c>[00 7C v2 v3][7C]</c>, whose +4 is the slot delimiter,
    /// is never mistaken for the header).</summary>
    public static bool IsExtraDataListHeader(ReadOnlySpan<byte> data, int p) =>
        Within(data, p, RefExtraHeaderLength) && data[p] == 0x00 && data[p + 1] == Delimiter
        && data[p + 4] != Delimiter && data[p + 6] == Delimiter;

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

    // ---- The bit2/bit10 (CHANGE_REFR_HAVOK_MOVE) pre-list Havok physics blob (ROADMAP §4i / §10) -------
    //
    // On late-game records the region between the MOVE block and the ExtraDataList is NOT the vanilla
    // 232-slot [4][7C] array — it is an active-Havok-physics blob. Characterised by corpus alignment over
    // all 113 bit2/bit10 records (VNV Extended ONLY — base VNV and vanilla have zero):
    //
    //   [preamble]                       7 bytes: [u16][7C][u8][7C][u8][7C]  (e.g. E1 10 7C 04 7C 4C 7C, or 49 11 7C 05 7C 4C 7C)
    //   N × HAVOK ENTRY (58 bytes each): [pos:3×f32][7C][quat:4×f32][7C][03][7C][vel:3×f32][7C][angvel:3×f32][7C]
    //   [truncated final entry]          pos+quat+[03] then trails off into zeros
    //   [trailing slot array]            a variable run of vanilla-style [4][7C] actor-value/havok slots
    //   [ExtraDataList header]           the item-list start
    //
    // It is GENUINELY variable-length (6 distinct blob lengths, scattered mod 5) with a truncated last entry
    // and a variable trailing slot array whose values locally COLLIDE with IsExtraDataListHeader (a slot with
    // high byte 0x00 matches), so it cannot be byte-sized to a fixed stride and the list end can't be found by
    // structure alone. The robust locator is therefore the self-validating ExtraDataList-header anchor
    // (FalloutSave.ScanForExtraDataListAnchor) — a structural sizer wouldn't remove the need for that
    // self-validation at the slot-array tail, so it's deliberately NOT used to find the list (ROADMAP §10).
    // HavokPhysicsEntryLength below recognises one full 58-byte entry (the confirmed grammar, pinned by a test)
    // for inspection and any future exact decode; it does not gate the live decoder.

    /// <summary>The per-entry type byte inside a Havok physics entry (a full, non-truncated entry).</summary>
    public const byte HavokEntryType = 0x03;

    /// <summary>Byte length of one full Havok physics entry:
    /// <c>[pos:3×f32][7C][quat:4×f32][7C][03][7C][vel:3×f32][7C][angvel:3×f32][7C]</c> = 58 bytes.</summary>
    public const int HavokEntryLength = 58;

    /// <summary>Returns <see cref="HavokEntryLength"/> (58) when <paramref name="data"/> at <paramref name="p"/>
    /// is a structurally-valid full Havok physics entry — the five <c>0x7C</c> delimiters at their fixed offsets
    /// (12, 29, 31, 44, 57) and the <c>0x03</c> type byte at offset 30 — else -1. Inspection/validation only
    /// (the variable, truncated blob isn't sized this way; the list start uses the self-validating anchor). The
    /// grammar is corpus-confirmed across the 113 bit2/bit10 records (ROADMAP §4i).</summary>
    public static int HavokPhysicsEntryLength(ReadOnlySpan<byte> data, int p)
    {
        if (!Within(data, p, HavokEntryLength))
            return -1;
        // pos(12) | quat(16) | [03] | vel(12) | angvel(12) |  -> delimiters at 12, 29, 31, 44, 57; type at 30.
        return data[p + 12] == Delimiter && data[p + 29] == Delimiter && data[p + 30] == HavokEntryType
            && data[p + 31] == Delimiter && data[p + 44] == Delimiter && data[p + 57] == Delimiter
            ? HavokEntryLength : -1;
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

    const byte RefListType = 0x5E;     // ExtraDataList ref-list entry: [5E][7C][N*4][7C] then N×(ref:3 7C flag:1 7C)
    const byte FixedBlockType = 0x18;  // 24-byte ref + position + rotation block
    const int FixedBlockLength = 24;
    const byte LinkedRefType = 0x74;   // a linked-ref entry just before the count
    const byte U32EntryType = 0x60;    // optional u32 entry on large inventories
    const byte SubRefListType = 0x1D;  // modded-only sub ref-list: [1D][7C][b][7C] then (ref:3 7C)×(b/4) — §4i
    const byte TwoRefType = 0x75;      // modded-only 2-ref entry: [75][7C][ref:3][7C][ref:3][7C][flag:1][7C] = 12 bytes

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
    /// the caller falls back to the forward scan — when the ExtraDataList isn't a recognised shape.
    /// <para>The list is a header + lead pair + a sequence of typed entries in <b>any order</b> (vanilla is
    /// <c>5E,18,74</c>(+<c>60</c>); modded VNV reorders to <c>18,74,5E,…</c> and adds <c>1D</c>/<c>75</c>),
    /// terminated by the inventory's <c>vsval</c> stack count whose value + delimiter lands on the first item.
    /// This walks entries via the shared <see cref="ExtraEntryLength"/> catalog (in lockstep with the inspection
    /// walk <see cref="WalkExtraDataList"/>) and recognises the terminating <c>vsval</c> by it being immediately
    /// followed by a structurally-valid stack (<see cref="LooksLikeStackStart"/>) — an entry-type byte can also be
    /// a small <c>vsval</c> value, so this strong "next is a real stack" test, checked first, disambiguates; the
    /// caller then confirms by walking ≥ <paramref name="stackCount"/> FormID-resolving stacks.</para>
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

        // 2. ExtraDataList lead pair [xx][7C] (a flag/count; meaning unpinned — only its delimiter is framing).
        if (!Within(data, p, 2) || data[p + 1] != Delimiter)
            return false;
        p += 2;

        // 3. typed entries in ANY order, terminated by the inventory stack count (vsval). Check the vsval
        //    termination first (it's the strong, self-validating anchor — value + 7C landing on a real stack);
        //    otherwise consume the next typed entry from the shared catalog.
        while (true)
        {
            if (TryReadInventoryVsval(data, p, out itemsOffset, out stackCount))
                return true;
            var len = ExtraEntryLength(data, p);
            if (len > 0)
            {
                p += len;
                continue;
            }
            // Neither a vsval nor a known entry: the variable post-entry tail (the 0x04/0x14/0x15 ref-lists,
            // ROADMAP §4i gap 2 — inconsistently framed, so not individually sized). Advance to the
            // self-validating vsval within a bounded window; the caller still confirms ≥ stackCount real stacks.
            for (var q = p + 1; q <= p + PostEntryResyncWindow; q++)
                if (TryReadInventoryVsval(data, q, out itemsOffset, out stackCount))
                    return true;
            return false;
        }
    }

    /// <summary>Bytes the post-entry tail resync (variable 0x04/0x14/0x15 ref-lists) will scan for the vsval
    /// before giving up; the observed tails are ≤ ~284 bytes.</summary>
    public const int PostEntryResyncWindow = 1024;

    /// <summary>Sanity bound on the inventory's <c>vsval</c> stack count. A real inventory holds at most a few
    /// hundred stacks (the largest observed across 607 saves is ~221); a value this large is a misread of entry
    /// data as a wide vsval, so it's rejected — keeping a coincidental 4-byte vsval from passing the resync.</summary>
    public const int MaxInventoryStacks = 100_000;

    /// <summary>True when <paramref name="p"/> is the inventory's terminating <c>vsval</c> stack count: a
    /// positive, sane (<see cref="MaxInventoryStacks"/>) value whose width + a <c>0x7C</c> delimiter lands on a
    /// structurally-valid first stack (<see cref="LooksLikeStackStart"/>). Outputs that first-stack offset and count.</summary>
    static bool TryReadInventoryVsval(ReadOnlySpan<byte> data, int p, out int itemsOffset, out int stackCount)
    {
        itemsOffset = -1;
        stackCount = -1;
        var count = ReadVsval(data, p, out var vlen);
        if (count <= 0 || count > MaxInventoryStacks || !Within(data, p, vlen + 1) || data[p + vlen] != Delimiter
            || !LooksLikeStackStart(data, p + vlen + 1))
            return false;
        stackCount = (int)count;
        itemsOffset = p + vlen + 1;
        return true;
    }

    /// <summary>Structural test that <paramref name="p"/> begins an inventory stack
    /// <c>[ref:3 BE][7C][count:u32 LE][7C]</c>: both delimiters present, a non-zero reference, and a count whose
    /// upper three bytes aren't the <c>0x7C</c> delimiter — the same shape <see cref="FalloutSave"/> validates,
    /// minus the FormID resolution, so it can recognise the <c>vsval</c>→items boundary without the masters.</summary>
    public static bool LooksLikeStackStart(ReadOnlySpan<byte> data, int p) =>
        Within(data, p, 9) && data[p + 3] == Delimiter && data[p + 8] == Delimiter
        && (data[p] | data[p + 1] | data[p + 2]) != 0
        && data[p + 5] != Delimiter && data[p + 6] != Delimiter && data[p + 7] != Delimiter;

    static bool Within(ReadOnlySpan<byte> data, int start, int length) => start >= 0 && (long)start + length <= data.Length;

    // ---- Generalised typed-entry ExtraDataList walk (RE inspection — ROADMAP §4i ◑, measure-first) -----
    //
    // INSPECTION-ONLY this iteration: TryInventoryItemsStart (above, the live decoder) is the vanilla
    // FIXED-sequence parse. Modded ExtraDataLists REORDER the entries and add new types, so the fixed parse
    // bails and the caller falls back to the §4g scan. To pin the modded grammar we walk the list as a
    // general TYPED-ENTRY sequence (any order) against a size catalog, bounded by a known first-item offset
    // (the §4g fallback already locates it correctly), and report how far the catalog gets + the first
    // unrecognised type. As types are pinned by aligning the corpus the catalog grows until every save is
    // "fully explained" — at which point the follow-up iteration swaps this in for the fixed parse.
    //
    // Grammar (consistent with the vanilla parse, which glued the lead pair to its first 0x5E entry):
    //   [00][7C][scale:f32][7C]   reference header (7 bytes)
    //   [xx][7C]                  ExtraDataList lead byte (a flag/count — meaning unpinned; 2 bytes)
    //   ( [type:u8][7C][payload] )*   typed entries, VARIABLE ORDER (catalog: ExtraEntryLength)
    //   [vsval][7C]               inventory stack count, then the item stacks

    /// <summary>One ExtraDataList typed entry: its offset (in the walk's span base), type byte, and total
    /// byte length including the <c>[type][7C]</c> framing.</summary>
    public readonly record struct ExtraEntry(int Offset, byte Type, int Length);

    /// <summary>The result of <see cref="WalkExtraDataList"/>. <see cref="FullyExplained"/> is true when the
    /// catalog consumed the header + lead + every entry and the trailing <c>vsval</c> lands exactly on
    /// <c>stopOffset</c> (the known first item). Otherwise <see cref="UnknownType"/> is the first
    /// unrecognised entry type (or null if the header/lead framing itself failed) and
    /// <see cref="UnexplainedBytes"/> is how many bytes between the stop point and the first item were not
    /// accounted for — the signal for pinning a type's size by alignment.</summary>
    public readonly record struct ExtraDataListWalk(
        bool HeaderOk, byte LeadByte, IReadOnlyList<ExtraEntry> Entries, int StopOffset,
        long Vsval, int VsvalOffset, bool FullyExplained, byte? UnknownType, int UnexplainedBytes);

    /// <summary>
    /// Total byte length of the ExtraDataList typed entry at <paramref name="p"/> (an entry is
    /// <c>[type:u8][7C][payload]</c>), or -1 if the type isn't in the catalog or the entry would run past
    /// <paramref name="data"/>. Sizes are pinned by aligning real saves (ROADMAP §4i); <c>0x1D</c> is a
    /// hypothesis under measurement. Offsets are relative to <paramref name="data"/>'s base.
    /// </summary>
    public static int ExtraEntryLength(ReadOnlySpan<byte> data, int p)
    {
        if (p < 0 || p + 2 > data.Length || data[p + 1] != Delimiter)
            return -1;
        switch (data[p])
        {
            case FixedBlockType: return Within(data, p, FixedBlockLength) ? FixedBlockLength : -1; // 0x18
            case LinkedRefType: return Within(data, p, 6) ? 6 : -1;                                // 0x74
            case U32EntryType: return Within(data, p, 7) ? 7 : -1;                                 // 0x60
            case TwoRefType: return Within(data, p, 12) ? 12 : -1;                                 // 0x75
            case RefListType:                                                                      // 0x5E: 4 + 6N
            {
                if (p + 4 > data.Length || data[p + 3] != Delimiter) return -1;
                var len = 4 + 6 * (data[p + 2] / 4);
                return Within(data, p, len) ? len : -1;
            }
            case SubRefListType:                                                                   // 0x1D: 4 + 4N (hyp.)
            {
                if (p + 4 > data.Length || data[p + 3] != Delimiter) return -1;
                var len = 4 + 4 * (data[p + 2] / 4);
                return Within(data, p, len) ? len : -1;
            }
            default: return -1;
        }
    }

    /// <summary>
    /// Walks the reference ExtraDataList from <paramref name="start"/> (<see cref="InventorySearchStart"/>)
    /// as a general typed-entry sequence, stopping at the known first-item offset <paramref name="stopOffset"/>.
    /// Inspection aid for pinning the modded grammar — it does not gate the live decoder. Offsets are relative
    /// to <paramref name="data"/>'s base (pass a record-relative span + record-relative offsets, or an
    /// absolute span + absolute offsets, consistently).
    /// </summary>
    public static ExtraDataListWalk WalkExtraDataList(ReadOnlySpan<byte> data, int start, int stopOffset)
    {
        var entries = new List<ExtraEntry>();
        var p = start;

        var headerOk = Within(data, p, RefExtraHeaderLength) && data[p + 1] == Delimiter && data[p + 6] == Delimiter;
        if (!headerOk)
            return new ExtraDataListWalk(false, 0, entries, p, -1, -1, false, null, Math.Max(0, stopOffset - p));
        p += RefExtraHeaderLength;

        // Lead pair [xx][7C] (a flag/count; unpinned). Required framing — a missing delimiter means the
        // structure isn't what we think, so bail rather than mis-walk.
        if (!Within(data, p, 2) || data[p + 1] != Delimiter)
            return new ExtraDataListWalk(true, 0, entries, p, -1, -1, false, null, Math.Max(0, stopOffset - p));
        var leadByte = data[p];
        p += 2;

        byte? unknown = null;
        long vsval = -1;
        var vsvalOffset = -1;
        while (p < stopOffset)
        {
            // The list terminates with the inventory's vsval stack count, whose value + its 0x7C lands
            // exactly on the first item. Check that first — otherwise the loop would try (and fail) to parse
            // the vsval byte as an entry type.
            var v = ReadVsval(data, p, out var vlen);
            if (v >= 0 && p + vlen + 1 == stopOffset && data[p + vlen] == Delimiter)
            {
                vsval = v;
                vsvalOffset = p;
                break;
            }

            var len = ExtraEntryLength(data, p);
            if (len <= 0)
            {
                // Report the offending type only when its [type][7C] framing is intact (else it's noise).
                unknown = p + 2 <= data.Length && data[p + 1] == Delimiter ? data[p] : (byte?)null;
                break;
            }
            entries.Add(new ExtraEntry(p, data[p], len));
            p += len;
        }

        var fully = vsvalOffset >= 0;
        return new ExtraDataListWalk(true, leadByte, entries, p, vsval, vsvalOffset, fully,
            fully ? null : unknown, fully ? 0 : Math.Max(0, stopOffset - p));
    }

    /// <summary>Human description of the set bits in a reference record's <c>changeFlags</c> — each as
    /// <c>bitN(NAME)</c> where the name is known (<see cref="LabelForBit"/>), else bare <c>bitN</c>.
    /// <paramref name="kind"/> disambiguates the context-dependent bits (10/11/12/17/21/22/23); with
    /// <see cref="RefKind.Unknown"/> those show both meanings as <c>actor|object</c>.</summary>
    public static string DescribeFlags(uint flags, RefKind kind = RefKind.Unknown)
    {
        var parts = new List<string>();
        for (var b = 0; b < 32; b++)
            if ((flags & (1u << b)) != 0)
                parts.Add(LabelForBit(b, kind) is { } name ? $"bit{b}({name})" : $"bit{b}");
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
    //
    // Four further types had their *payload length* pinned structurally by a CORPUS-ALIGNMENT measurement
    // (CLI `edlscan` over vanilla + base VNV + VNV Extended = 607 saves): for each occurrence, the byte gap
    // from the property's [type][7C] header to the next valid stack was histogrammed, cleanest when the
    // property is the block's LAST one (block ends → next stack). Each spiked at a single gap, so the length
    // is fixed (payload = gap==2 ? 0 : gap-3). Lengths only — the SEMANTICS stay unlabeled (per "label, don't
    // guess"), exactly as 0x21's was sized before its meaning was known:
    //   0x6E ExtraFlag6E     : 0-byte flag                — gap 2 on 929/929 (Extended; modded weapons)
    //   0x1C ExtraRef1C      : 3-byte BE refID            — gap 6 on 108/108
    //   0x24 ExtraU16_24     : 2-byte value (a small u16) — gap 5 on 1163/1169 (+ a 0x25 condition often follows)
    //   0x30 ExtraFloat30    : 4-byte LE float            — gap 7 (last) / 12 when a 0x24 follows; a 0.82-ish float
    //   0x0D … : STRUCTURED/variable — now DECODED (sized) by VariablePropertyLength below; see its summary.
    // See ROADMAP §4i.

    public const byte Extra0D = 0x0D;
    public const byte ExtraEquipped = 0x16;
    public const byte ExtraRef1C = 0x1C;
    public const byte ExtraWeaponMod = 0x21;
    public const byte ExtraU16_24 = 0x24;
    public const byte ExtraCondition = 0x25;
    public const byte ExtraFloat30 = 0x30;
    public const byte ExtraFlag6E = 0x6E;

    /// <summary>Fixed payload length (bytes) of a per-stack extra-data property <paramref name="type"/>,
    /// or -1 if the type carries a variable/structured payload we can't yet size deterministically.</summary>
    public static int FixedPropertyPayload(byte type) => type switch
    {
        ExtraEquipped => 0, // 0x16
        ExtraFlag6E => 0,   // 0x6E — corpus-pinned (gap 2 on 929/929)
        ExtraU16_24 => 2,   // 0x24 — corpus-pinned (gap 5 on 1163/1169)
        ExtraWeaponMod => 3, // 0x21
        ExtraRef1C => 3,    // 0x1C — corpus-pinned (gap 6 on 108/108)
        ExtraCondition => 4, // 0x25
        ExtraFloat30 => 4,  // 0x30 — corpus-pinned (gap 7 last / 12 with a trailing 0x24)
        _ => -1,
    };

    /// <summary>
    /// Total byte length (including the leading <c>[type][7C]</c>) of a <b>structured</b> per-stack
    /// extra-data property at <paramref name="p"/> whose size is computed from its own self-describing
    /// fields, or -1 if it isn't such a type or the structure is malformed/truncated. These are the types
    /// <see cref="FixedPropertyPayload"/> can't size with a single fixed length.
    /// <para>Currently only <c>0x0D</c>, pinned by corpus alignment across all 607 saves (its recovered
    /// lengths form an exact <c>12 + 14·(n/4)</c> progression — 12, 26, 54, 68, 110 … for 0, 1, 3, 4, 7
    /// pairs):</para>
    /// <code>[0D][7C] [ref:3 BE][7C] [n:u8][7C] (n/4)×( [u32:4][7C][f64:8][7C] ) [00][7C][00][7C]</code>
    /// i.e. a 3-byte refID, a <c>×4</c> pair count, that many <c>(u32, double)</c> pairs, then two fixed
    /// trailing fields. Length-only — the semantics stay unlabelled per the repo's "size, don't guess" rule.
    /// </summary>
    public static int VariablePropertyLength(ReadOnlySpan<byte> data, int p)
    {
        if (p < 0 || p >= data.Length || data[p] != Extra0D)
            return -1;
        // [0D][7C] [ref:3][7C] [n:u8][7C]
        if (!Within(data, p, 8) || data[p + 1] != Delimiter || data[p + 5] != Delimiter || data[p + 7] != Delimiter)
            return -1;
        var pairs = data[p + 6] / 4;
        var q = p + 8;
        for (var i = 0; i < pairs; i++) // each pair: [u32:4][7C][f64:8][7C] = 14 bytes
        {
            if (!Within(data, q, 14) || data[q + 4] != Delimiter || data[q + 13] != Delimiter)
                return -1;
            q += 14;
        }
        // two trailing [xx][7C] fields (always 00 on every observed save; we frame on the delimiters only)
        if (!Within(data, q, 4) || data[q + 1] != Delimiter || data[q + 3] != Delimiter)
            return -1;
        return q + 4 - p;
    }

    /// <summary>Decoded per-stack extra data plus the block's byte length. <see cref="FullyDecoded"/> is
    /// false when an unknown/variable property type was hit, in which case <see cref="ByteLength"/> only
    /// covers the decoded prefix and the caller must resynchronise to the next item; <see cref="UnknownType"/>
    /// is that first unsized property type (for RE histograms — ROADMAP §4i), or null when fully decoded or
    /// stopped by truncation rather than an unknown type. <see cref="UnknownOffset"/> is the absolute file
    /// offset of that unsized property's <c>[type][7C]</c> header (for the RE corpus-alignment sizer), or null.</summary>
    public readonly record struct StackExtra(
        int ByteLength, float? Condition, int? ConditionOffset, bool Equipped, IReadOnlyList<int> ModRefIds, bool FullyDecoded, byte? UnknownType, int? UnknownOffset)
    {
        public static readonly StackExtra None = new(2, null, null, false, [], true, null, null);
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
        var fully = true; byte? unknownType = null; int? unknownOffset = null;
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
                // Not a single-fixed-length type: try the structured sizer (currently 0x0D). When it sizes,
                // advance by the whole entry and keep decoding — the property is opaque to us (length only).
                var vlen = VariablePropertyLength(data, cur);
                if (vlen > 0)
                {
                    cur += vlen;
                    continue;
                }
                fully = false; // structured/unknown — can't size the rest deterministically
                unknownType = type;
                unknownOffset = dataOffset + cur;
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
        extra = new StackExtra(cur - p, condition, conditionOffset, equipped, (IReadOnlyList<int>?)mods ?? [], fully, unknownType, unknownOffset);
        return true;
    }
}
