using System.Buffers.Binary;
using System.Text;
using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PlayerNotesTests
{
    [Fact]
    public void Synthetic_decodes_only_the_read_note_markers()
    {
        var save = FalloutSave.Parse(NotesSave.Build());

        // Two read-note markers were planted (on the forms at array index 1 and 3); the third change form is a
        // decoy (wrong type/flags) and must be ignored. A marker's iref is the note's index + 1, so the note
        // FormID is FormIdArray[iref - 1].
        var notes = save.ReadNotes;
        Assert.Equal(2, notes.Count);
        var formIds = notes.Notes.Select(n => n.FormId).OrderBy(f => f).ToArray();
        Assert.Equal([0x000A0001u, 0x000A0002u], formIds);
    }

    [Fact]
    public void Synthetic_marker_iref_resolves_to_the_note_one_slot_earlier()
    {
        var save = FalloutSave.Parse(NotesSave.Build());

        var note = save.ReadNotes.Notes.Single(n => n.FormId == 0x000A0001);
        Assert.Equal(2, note.MarkerIref);                     // index 1 + 1
        Assert.Equal(save.ResolveIref(note.MarkerIref), note.MarkerFormId);
        Assert.Equal(save.ResolveIref(note.MarkerIref - 1), note.FormId);
    }

    [Fact]
    public void Notes_decode_is_read_only_round_trip_identical()
    {
        var original = NotesSave.Build();
        var save = FalloutSave.Parse(original);
        _ = save.ReadNotes;                                   // reading notes must not stage any edit
        Assert.False(save.HasPendingEdits);
        Assert.Equal(original, save.ToBytes());
    }

    [Fact]
    public void ReadNotes_is_empty_not_null_when_no_markers()
    {
        // The synthetic inventory save carries no note markers — ReadNotes must be a present, empty list.
        var save = FalloutSave.Parse(InventorySave.Build());

        Assert.NotNull(save.ReadNotes);
        Assert.Empty(save.ReadNotes.Notes);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_read_notes_are_well_formed_and_read_only(string path)
    {
        var save = FalloutSave.Load(path);
        var notes = save.ReadNotes;          // never null
        Assert.NotNull(notes);

        Assert.All(notes.Notes, n =>
        {
            Assert.True(n.MarkerIref > 0);                               // index + 1, never the reserved 0
            Assert.NotEqual(0u, n.FormId);                              // FormIdArray[iref - 1] resolved
            Assert.Equal(save.ResolveIref(n.MarkerIref - 1), n.FormId); // the §4k +1 convention
        });
        // Markers reference distinct notes (each note is read at most once).
        Assert.Equal(notes.Notes.Select(n => n.MarkerIref).Distinct().Count(), notes.Count);

        // Decoding notes is read-only: it must not alter the file.
        Assert.Equal(save.FileLength, save.ToBytes().Length);
        Assert.Equal(File.ReadAllBytes(path), save.ToBytes());
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_every_type_0x1F_change_form_is_a_read_note_marker(string path)
    {
        // Corpus-verified invariant (ROADMAP §4k.1 #1/#2, `notescan` over 45,783 markers across vanilla +
        // base VNV + VNV Extended): every change form of form-type 0x1F is a read-note marker — changeFlags
        // EXACTLY 0x80000000 and a zero-length payload — and its note resolves via the −1 index. So the
        // ReadNotes filter (type 0x1F + flags 0x80000000 + len 0) can neither miss nor over-match a 0x1F form.
        var save = FalloutSave.Load(path);

        var type1F = save.EnumerateChangeForms().Where(cf => cf.FormType == 0x1F).ToList();
        Assert.All(type1F, cf =>
        {
            Assert.Equal(0x80000000u, cf.ChangeFlags);                 // #1: always exactly this flag value
            Assert.Equal(0, cf.DataLength);                            // zero-payload marker
            Assert.True(cf.Iref > 0);                                  // #3: the +1 convention (never iref 0)
            Assert.NotEqual(0u, save.ResolveIref(cf.Iref - 1));        // the note one slot earlier resolves
        });

        // Every type-0x1F form is captured as a read note — the filter drops none of them.
        Assert.Equal(type1F.Count, save.ReadNotes.Count);
    }

    [Fact]
    public void Pip_boy_notes_includes_read_markers_when_no_inventory_record()
    {
        // The synthetic notes save has read markers but no player inventory record (no PlayerRef 0x14), so the
        // full notes list (PipBoyNotes, §4k.1 #4) is just the read-marker union: both notes, each flagged Read,
        // independent of the NOTE predicate.
        var save = FalloutSave.Parse(NotesSave.Build());
        var notes = save.PipBoyNotes(_ => false);
        Assert.Equal([0x000A0001u, 0x000A0002u], notes.Select(n => n.FormId).OrderBy(f => f).ToArray());
        Assert.All(notes, n => Assert.True(n.Read));
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Pip_boy_notes_is_a_superset_of_read_notes(string path)
    {
        // Masters-free invariant: restrict the NOTE predicate to FormIDs that already carry a read marker, so
        // PipBoyNotes must return exactly those, every one flagged Read (the markers are the authoritative read
        // set and are always included). Validates the acquired-scan ∪ read-marker union + read classification.
        var save = FalloutSave.Load(path);
        var readFormIds = save.ReadNotes.Notes.Select(n => n.FormId).ToHashSet();
        var notes = save.PipBoyNotes(readFormIds.Contains);

        Assert.Equal(readFormIds, notes.Select(n => n.FormId).ToHashSet());
        Assert.All(notes, n => Assert.True(n.Read));
    }

    [Fact]
    public void Pip_boy_notes_finds_the_acquired_unread_note_and_its_flip_to_read()
    {
        // Controlled triple (Saves 38→39→40, Doc Mitchell's House): Philippe's Recipes (0x00117E37) was added
        // unread via the console, then read. Masters-free — the predicate accepts exactly that FormID. Skips
        // gracefully when the triple isn't on this machine (ROADMAP §4k.1 #4).
        const uint philippes = 0x00117E37;
        bool IsPhilippes(uint fid) => fid == philippes;

        string? Triple(string n) => FalloutSaveTests.RealSaves()
            .Select(o => (string)o[0])
            .FirstOrDefault(p => Path.GetFileName(p).StartsWith($"Save {n} ", StringComparison.OrdinalIgnoreCase)
                && p.Contains("Doc Mitchell", StringComparison.OrdinalIgnoreCase));

        var (a, b, c) = (Triple("38"), Triple("39"), Triple("40"));
        if (a is null || b is null || c is null)
            return; // controlled triple not present — nothing to assert

        Assert.DoesNotContain(FalloutSave.Load(a).PipBoyNotes(IsPhilippes), n => n.FormId == philippes);
        Assert.False(FalloutSave.Load(b).PipBoyNotes(IsPhilippes).Single(n => n.FormId == philippes).Read); // acquired, unread
        Assert.True(FalloutSave.Load(c).PipBoyNotes(IsPhilippes).Single(n => n.FormId == philippes).Read);  // now read
    }
}

/// <summary>
/// Builds a minimal New Vegas <c>.fos</c> whose change-forms region holds two note "read" markers
/// (<c>[refID:3][changeFlags:0x80000000][type:0x1F][version][len:0]</c>) plus one decoy change form, so the
/// read-notes decoder (ROADMAP §4k) can be exercised without a real save. Each marker's <c>refID</c> is a
/// note's FormID-array index + 1.
/// </summary>
internal static class NotesSave
{
    public static byte[] Build()
    {
        var b = new List<byte>();
        void Str(string s) => b.AddRange(Encoding.Latin1.GetBytes(s));
        void Delim() => b.Add(0x7C);
        void U16(ushort v) { var t = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(t, v); b.AddRange(t); }
        void U32(uint v) { var t = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(t, v); b.AddRange(t); }
        void StringField(string s) { U16((ushort)s.Length); Delim(); Str(s); Delim(); }

        Str("FO3SAVEGAME");
        var headerSizeAt = b.Count;
        U32(0);            // saveHeaderSize placeholder (patched below)
        U32(0x30);         // version
        Delim();
        Str("ENGLISH"); Delim();
        U32(4); Delim();   // width
        U32(2); Delim();   // height
        U32(7); Delim();   // save number
        StringField("Test");
        StringField("Title");
        U32(3); Delim();   // level
        StringField("Vault1");
        StringField("000.01.02");

        var screenshotStart = b.Count;
        for (var i = 0; i < 4 * 2 * 3; i++) b.Add((byte)i);

        b.Add(0x15);       // trailer
        U32(0);            // pluginStructSize
        b.Add(1); Delim(); // one plugin
        StringField("A.esm");

        // ---- Body: File Location Table, FormID array, then three change forms ----
        var bodyStart = b.Count;
        var formIdArrayOffset = bodyStart + 32;             // right after the 8-u32 FLT
        uint[] formIds =
        [
            0x00000007,           // iref 0
            0x000A0001,           // iref 1 -> note A (read marker's refID is iref 2)
            0x0000AAAA,           // iref 2 -> the marker form for note A
            0x000A0002,           // iref 3 -> note B (read marker's refID is iref 4)
            0x0000BBBB,           // iref 4 -> the marker form for note B
            0x0000CCCC,           // iref 5 -> the decoy's form
        ];
        var changeFormsOffset = formIdArrayOffset + 4 + formIds.Length * 4;

        // Three change forms: two note-read markers (type 0x1F, flags 0x80000000, len 0) on irefs 2 and 4,
        // and one decoy (a generic flag change, type 0x00) on iref 5 that must be ignored.
        var cfs = new List<byte>();
        void Marker(int refIref)
        {
            cfs.Add((byte)(refIref >> 16)); cfs.Add((byte)(refIref >> 8)); cfs.Add((byte)refIref); // refID
            cfs.AddRange([0x00, 0x00, 0x00, 0x80]);  // changeFlags = 0x80000000 (LE)
            cfs.Add(0x1F);                            // type: form 0x1F, high bits 00 -> u8 length
            cfs.Add(0x1B);                            // version
            cfs.Add(0x00);                            // length 0 (no payload)
        }
        Marker(2);
        Marker(4);
        // Decoy: iref 5, generic CHANGE_FORM_FLAGS (type 0x00), a 1-byte payload — must not be read as a note.
        cfs.AddRange([0x00, 0x00, 0x05]);             // refID = iref 5
        cfs.AddRange([0x01, 0x00, 0x00, 0x00]);       // changeFlags = 0x00000001
        cfs.Add(0x00);                                // type 0x00 -> u8 length
        cfs.Add(0x1B);                                // version
        cfs.Add(0x01);                                // length 1
        cfs.Add(0xAB);                                // payload

        var globalData3Offset = changeFormsOffset + cfs.Count;

        // FLT (8 u32) — must sit exactly at bodyStart.
        U32((uint)formIdArrayOffset);  // [0] FormIdArrayCountOffset
        U32((uint)globalData3Offset);  // [1] UnknownTable3Offset
        U32((uint)formIdArrayOffset);  // [2] GlobalData1Offset (unused here)
        U32((uint)changeFormsOffset);  // [3] ChangeFormsOffset
        U32((uint)globalData3Offset);  // [4] GlobalData3Offset (= end of change forms)
        U32(0);                        // [5] GlobalData1Count
        U32(0);                        // [6] GlobalData3Count
        U32(3);                        // [7] ChangeFormCount

        U32((uint)formIds.Length);     // FormID array: count
        foreach (var f in formIds) U32(f);

        b.AddRange(cfs);               // change-forms region (two markers + one decoy)

        var bytes = b.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(headerSizeAt, 4), (uint)(screenshotStart - 15));
        return bytes;
    }
}
