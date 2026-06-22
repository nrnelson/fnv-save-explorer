namespace FnvSaveExplorer.Core;

/// <summary>
/// One note the player has <b>read/viewed</b> (Pip-Boy <i>Data → Notes</i>, shown in normal font rather
/// than bold). Reading a note makes the engine write a tiny change form on the note's inventory
/// reference — <c>[refID:3][changeFlags:0x80000000][type:0x1F][version][length:0]</c>, zero payload —
/// whose presence <i>is</i> the "read" state (ROADMAP §4k). The marker's <c>refID</c> is the note's
/// inventory reference, i.e. the FormID-array index <b>+ 1</b> (the same convention as inventory item
/// stacks, §4g), so the note's own FormID is <c>FormIdArray[markerIref - 1]</c> — which resolves to a
/// <c>NOTE</c> record. Display names are resolved separately from the game's masters
/// (<see cref="PluginDatabase"/>), exactly like inventory items.
/// </summary>
public sealed record NoteEntry(int MarkerIref, uint MarkerFormId, uint FormId)
{
    /// <summary>The mod (plugin load-order) index of the note's FormID — its high byte.</summary>
    public int ModIndex => (int)(FormId >> 24);
}

/// <summary>
/// The player's <b>read notes</b> — the notes marked viewed in the Pip-Boy <i>Data → Notes</i> tab,
/// decoded from the per-note "read" change-form markers (see <see cref="NoteEntry"/>). This is the set
/// the save records explicitly; notes that have been acquired but never opened leave no marker and so are
/// not represented here (ROADMAP §4k / §9). Read-only: the marker is a whole change form, so toggling
/// read state is a length-changing edit (deferred — §6.7), not a same-length splice.
/// </summary>
public sealed class PlayerNotes
{
    /// <summary>Every read-note marker, in change-form (file) order.</summary>
    public IReadOnlyList<NoteEntry> Notes { get; }

    public PlayerNotes(IReadOnlyList<NoteEntry> notes) => Notes = notes;

    /// <summary>The number of read notes.</summary>
    public int Count => Notes.Count;

    public override string ToString() => $"{Count} read note(s)";
}
