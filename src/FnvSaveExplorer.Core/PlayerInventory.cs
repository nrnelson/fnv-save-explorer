namespace FnvSaveExplorer.Core;

/// <summary>
/// One stack in the player's inventory, decoded from the inventory change form (see
/// <see cref="PlayerInventory"/>). Each entry is <c>[itemIref:3 BE][0x7C][count:u32 LE][0x7C]</c>
/// optionally followed by per-stack extra data (condition / equip state / etc., not yet decoded).
/// <see cref="CountValueOffset"/> is the absolute file offset of the 4-byte little-endian count — the
/// editable field (editing it is a safe same-length splice).
/// </summary>
public sealed record InventoryItem(int Iref, uint FormId, uint Count, int CountValueOffset)
{
    /// <summary>The mod (plugin load-order) index of the item's FormID — its high byte.</summary>
    public int ModIndex => (int)(FormId >> 24);
}

/// <summary>
/// The player's inventory, decoded from the player's inventory change form. In a New Vegas save the
/// player's carried items live in a dedicated reference change form (type 0x41) — distinct from the
/// PlayerRef (ACHR, 0x14) record, which holds actor state, not items. Inside that record's data, after
/// a 3D/position preamble, the items are a run of entries:
/// <code>[itemIref:3 BE][0x7C][count:u32 LE][0x7C] (extra-data…)</code>
///
/// <para><b>Verified</b> by a controlled drop-1 diff: dropping one of a stacked item decremented exactly
/// one entry's <c>count</c> (9 → 8) as a little-endian u32 — so editing a count is a safe same-length
/// splice. Items are referenced by <i>iref</i> (an index into the FormID array); the tool surfaces the
/// resolved FormID and its mod index, and display names are resolved separately from the game's ESM/ESP
/// masters via <see cref="PluginDatabase"/>.</para>
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
