using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>
/// Player addictions / actor added-spell list — the gated-section decode of the player CHANGE_ACTOR change form
/// (form type 0x0A, iref = playerBase 0x07 + 1; SPEC §4n). The record holds an added-spell list (addictions live
/// here as Withdrawal SPELs), SPECIAL, name, and optional AI package data, with sections gated by changeFlags
/// (bit 1 MOVE before the list, bit 11 package data after the name). Cracked by the <c>beadley-addiction-*</c>
/// controlled FIFO diff.
/// </summary>
public class AddictionTests
{
    // ---- ChangeActorPayload unit tests on synthetic payloads (pure decoder) ------------------------

    /// <summary>Builds a synthetic CHANGE_ACTOR payload: optional 24-byte MOVE block, a spell list of
    /// <paramref name="spellRefs"/>, SPECIAL (sums to 40), a name, and optional trailing package bytes.</summary>
    private static byte[] BuildActorPayload(bool move, int[] spellRefs, string name, int packageBytes)
    {
        var b = new List<byte>();
        if (move)
        {
            b.AddRange(new byte[ChangeActorPayload.MoveBlockLength]); // 24-byte block (content irrelevant)
            b.Add(0x7C);
        }
        b.Add((byte)(spellRefs.Length * 4)); // count×4
        b.Add(0x7C);
        foreach (var r in spellRefs)
        {
            b.Add((byte)(r >> 16)); b.Add((byte)(r >> 8)); b.Add((byte)r); // 3-byte BE refID
            b.Add(0x7C);
        }
        b.Add(0x00); b.Add(0x7C);                                          // [00][7C] trailer
        b.AddRange(new byte[] { 5, 5, 5, 5, 5, 5, 10 }); b.Add(0x7C);      // SPECIAL (sum 40) + 7C
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
        b.Add((byte)nameBytes.Length); b.Add((byte)(nameBytes.Length >> 8)); b.Add(0x7C);
        b.AddRange(nameBytes); b.Add(0x7C);
        b.AddRange(new byte[packageBytes]);
        return [.. b];
    }

    [Theory]
    [InlineData(false, 0)]  // no MOVE, no package — minimal record (the Beadley 0x036/no-bit11 shape)
    [InlineData(true, 0)]   // MOVE present (bit 1)
    [InlineData(true, 64)]  // MOVE + package data (bit 11) — the Nathan/MW shape
    [InlineData(false, 40)] // package only
    public void Decodes_and_fully_consumes_synthetic_records(bool move, int package)
    {
        uint flags = (move ? ReferenceChangeForm.ChangeRefrMove : 0u)
                   | (package > 0 ? ReferenceChangeForm.ActorPackageData : 0u);
        var data = BuildActorPayload(move, [0x0030BB, 0x0030BC], "Beadley", package);

        Assert.True(ChangeActorPayload.TryDecode(data, flags, out var rec));
        Assert.True(rec.FullyConsumed, "section walk must consume the whole payload exactly");
        Assert.Equal(move, rec.MoveBlockPresent);
        Assert.Equal([0x0030BB, 0x0030BC], rec.Spells.Select(s => s.RefId).ToArray());
        Assert.Equal("Beadley", rec.Name);
        Assert.Equal(40, rec.Special.Sum(x => x));
        Assert.Equal(package, rec.PackageDataLength);
    }

    [Fact]
    public void Empty_spell_list_decodes()
    {
        var data = BuildActorPayload(move: true, spellRefs: [], name: "Nobody", packageBytes: 0);
        Assert.True(ChangeActorPayload.TryDecode(data, ReferenceChangeForm.ChangeRefrMove, out var rec));
        Assert.True(rec.FullyConsumed);
        Assert.Empty(rec.Spells);
    }

    [Fact]
    public void Declines_on_implausible_special()
    {
        // A SPECIAL byte outside 1..10 means we mis-located the section — TryDecode must decline rather than
        // read garbage (the graceful path the SPECIAL/karma locators take). With no MOVE and an empty list
        // (`00 7C 00 7C`), SPECIAL begins at offset 4; zero its first byte.
        var data = BuildActorPayload(move: false, spellRefs: [], name: "X", packageBytes: 0);
        data[4] = 0;
        Assert.False(ChangeActorPayload.TryDecode(data, 0, out _));
    }

    [Fact]
    public void Package_flag_clear_requires_zero_remainder()
    {
        // bit 11 clear but trailing bytes present → not fully consumed (the full-length invariant catches a
        // section-sizing error).
        var data = BuildActorPayload(move: false, spellRefs: [], name: "X", packageBytes: 8);
        Assert.True(ChangeActorPayload.TryDecode(data, 0, out var rec)); // structure still parses
        Assert.False(rec.FullyConsumed);                                 // but bytes are unaccounted for
    }

    // ---- Controlled FIFO ground truth (beadley-addiction-*) ----------------------------------------

    [Fact]
    public void Beadley_addiction_fifo_decodes_known_spells()
    {
        // The 7-save FIFO (add Buffout → Alcohol → Med-X, then remove FIFO until recovered) pins the added-spell
        // list exactly. FormIDs are FalloutNV.esm base forms (stable), so this is masters-free — it asserts the
        // refs resolve via the §4g "−1" index to the right Withdrawal SPELs. Skipped off the dev box.
        const uint buffout = 0x00033066, alcohol = 0x0006698C, medx = 0x0002AE29;
        var byName = FalloutSaveTests.RealSaves()
            .Select(o => (string)o[0])
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        void Check(string save, params uint[] expected)
        {
            if (!byName.TryGetValue(save, out var path))
                return;
            var spells = FalloutSave.Load(path).PlayerActorSpells(_ => true).Select(s => s.FormId).ToHashSet();
            // The actor list may also carry traits/abilities; assert the addiction FormIDs are present/absent.
            foreach (var e in new[] { buffout, alcohol, medx })
                Assert.Equal(expected.Contains(e), spells.Contains(e));
        }

        Check("beadley-addiction-pre");                            // none
        Check("beadley-addiction-buffout", buffout);
        Check("beadley-addiction-buffout-alcohol", buffout, alcohol);
        Check("beadley-addiction-buffout-alcohol-medx", buffout, alcohol, medx);
        Check("beadley-addiction-medx", medx);                    // FIFO dropped buffout + alcohol
        Check("beadley-addiction-alcohol-medx", alcohol, medx);
        Check("beadley-addiction-recovered");                     // none
    }

    [Fact]
    public void Beadley_fifo_addiction_filter_uses_masters()
    {
        // Masters-gated: with the Data folder present, IsAddiction (SPEL spell-type 10) selects exactly the three
        // withdrawal effects on the all-addictions save, and the trait/ability slots are excluded.
        var byName = FalloutSaveTests.RealSaves()
            .Select(o => (string)o[0])
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        if (!byName.TryGetValue("beadley-addiction-buffout-alcohol-medx", out var path))
            return;
        var save = FalloutSave.Load(path);
        var db = PluginDatabase.ForSave(save);
        if (db.Count == 0)
            return; // masters not on this machine

        var addictions = save.PlayerActorSpells(db.IsAddiction).Select(s => s.FormId).ToHashSet();
        Assert.Equal(new HashSet<uint> { 0x00033066, 0x0006698C, 0x0002AE29 }, addictions);
        Assert.All(addictions, fid => Assert.Equal(PluginDatabase.SpellTypeAddiction, db.SpellType(fid)));
    }

    // ---- Corpus determinism: the section walk consumes the whole payload (Stage 1 acceptance bar) ----

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Player_actor_record_section_walk_is_complete(string path)
    {
        // Where the player CHANGE_ACTOR record is locatable, its gated-section walk must parse AND consume the
        // whole payload exactly (the §4f "lands exactly" analogue): SPECIAL plausible, name non-empty, every
        // byte attributed. A misdecode would leave FullyConsumed false or fail TryDecode.
        var save = FalloutSave.Load(path);
        if (save.PlayerActorChangeForm is not { } cf)
            return; // not locatable on this save — declines gracefully like karma/limbs

        var data = save.ReadAt(cf.DataOffset, cf.DataLength);
        Assert.True(ChangeActorPayload.TryDecode(data, cf.ChangeFlags, out var rec),
            $"CHANGE_ACTOR did not decode ({Path.GetFileName(path)})");
        Assert.True(rec.FullyConsumed,
            $"CHANGE_ACTOR section walk left bytes unaccounted ({Path.GetFileName(path)})");
        Assert.InRange(rec.Special.Sum(x => x), 7, 80); // 7 stats, each 1..10
        Assert.NotEmpty(rec.Name);

        // Reads never throw and resolve to real forms; addictions are a (possibly empty) subset.
        var all = save.PlayerActorSpells(_ => true);
        Assert.All(all, s => Assert.NotEqual(0u, s.FormId));
    }
}
