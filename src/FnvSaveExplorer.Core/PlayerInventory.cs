namespace FnvSaveExplorer.Core;

/// <summary>
/// One stack in the player's inventory, decoded from the inventory change form (see
/// <see cref="PlayerInventory"/>). On disk an entry is <c>[ref:3 BE][0x7C][count:u32 LE][0x7C]</c>
/// optionally followed by per-stack extra data (condition / equip state / etc., not yet decoded). The
/// 3-byte <c>ref</c> is the FormID-array index <b>plus one</b>, so <see cref="FormId"/> is
/// <c>FormIdArray[ref - 1]</c> and <see cref="Iref"/> is that index; the <c>count</c> belongs to this
/// entry. <see cref="CountValueOffset"/> is the absolute file offset of the 4-byte little-endian count —
/// the editable field (editing it is a safe same-length splice).
/// </summary>
public sealed record InventoryItem(int Iref, uint FormId, uint Count, int CountValueOffset)
{
    /// <summary>The mod (plugin load-order) index of the item's FormID — its high byte.</summary>
    public int ModIndex => (int)(FormId >> 24);

    /// <summary>The stack's condition/health (a weapon/armor's degradation float), or null if the stack
    /// carries no condition extra-data (ammo, aid, misc, or undamaged-and-never-tracked items).</summary>
    public float? Condition { get; init; }

    /// <summary>Absolute file offset of the 4-byte little-endian <see cref="Condition"/> float — the
    /// editable field (a safe same-length splice), or null when the stack has no condition.</summary>
    public int? ConditionValueOffset { get; init; }

    /// <summary>True when the stack is equipped/worn (the <c>0x16</c> extra-data flag is present).</summary>
    public bool Equipped { get; init; }

    /// <summary>FormID-array irefs carried by the stack's <c>0x21</c> extra-data properties — an attached
    /// <b>weapon mod</b> when the stack is a weapon, though the type's full semantics aren't pinned (some
    /// mods reuse the slot for other linked references, e.g. a "Bill of Sale" note on a consumable).</summary>
    public IReadOnlyList<int> ExtraRefIds { get; init; } = [];
}

/// <summary>
/// The player's inventory, decoded from the player's inventory change form. In a New Vegas save the
/// player's carried items live in a dedicated reference change form (type 0x41) — distinct from the
/// PlayerRef (ACHR, 0x14) record, which holds actor state, not items. Inside that record's data, after
/// a 3D/position preamble, the items are a run of entries:
/// <code>[ref:3 BE][0x7C][count:u32 LE][0x7C] (extra-data…)</code>
/// where <c>ref</c> is the FormID-array index <b>+ 1</b> (so the item is <c>FormIdArray[ref - 1]</c>) and
/// <c>count</c> is the entry's own stack count.
///
/// <para><b>Verified</b> by a controlled in-game diff: adding then consuming one Antivenom moved exactly
/// one entry's <c>count</c> (1 → 2 → 1) as a little-endian u32, and the entry's <c>ref</c> resolved to
/// Antivenom only via the <c>- 1</c> index — confirming both the <c>+ 1</c> reference encoding and that the
/// count is same-length editable. Items are referenced by <i>iref</i> (the FormID-array index); the tool
/// surfaces the resolved FormID and its mod index, and display names are resolved separately from the
/// game's ESM/ESP masters via <see cref="PluginDatabase"/>.</para>
/// </summary>
public sealed class PlayerInventory
{
    /// <summary>Absolute file offset of the inventory change form's data (the record holding the items).</summary>
    public int Offset { get; }

    /// <summary>Every decoded inventory stack, in file order.</summary>
    public IReadOnlyList<InventoryItem> Items { get; }

    public PlayerInventory(int offset, IReadOnlyList<InventoryItem> items)
    {
        Offset = offset;
        Items = items;
    }

    /// <summary>The total number of individual items across all stacks.</summary>
    public long TotalItems => Items.Sum(i => (long)i.Count);

    public override string ToString() =>
        $"{Items.Count} stacks, {TotalItems} items";
}
