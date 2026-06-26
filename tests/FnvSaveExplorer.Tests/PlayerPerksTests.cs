using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PlayerPerksTests
{
    private static string? SaveNamed(string fileNamePrefix) => FalloutSaveTests.RealSaves()
        .Select(o => (string)o[0])
        .FirstOrDefault(p => Path.GetFileName(p).StartsWith(fileNamePrefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Controlled_diff_perk_appears_only_after_it_is_taken()
    {
        // gtg-complete -> level2-gunsbachelor: the player took "Confirmed Bachelor" (0x001361B4). Masters-free —
        // the predicate accepts exactly that FormID; PlayerPerks must find it ONLY in the post save (ROADMAP §4n).
        // Taking the perk appended its FormID to the array, so before, no perk ref resolves to it.
        const uint confirmedBachelor = 0x001361B4;
        bool IsCb(uint fid) => fid == confirmedBachelor;

        var (pre, post) = (SaveNamed("gtg-complete"), SaveNamed("level2-gunsbachelor"));
        if (pre is null || post is null)
            return; // controlled pair not present on this machine — nothing to assert

        Assert.DoesNotContain(FalloutSave.Load(pre).PlayerPerks(IsCb), p => p.FormId == confirmedBachelor);

        var taken = FalloutSave.Load(post).PlayerPerks(IsCb).Single();
        Assert.Equal(confirmedBachelor, taken.FormId);
        Assert.Equal(1, taken.Rank);                                   // single-rank perk
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_perk_decode_is_well_formed_and_read_only(string path)
    {
        var save = FalloutSave.Load(path);

        // Accept-all predicate exercises the full scan; every hit must obey the §4n structure: a positive refId
        // whose FormID is exactly FormIdArray[refId - 1] (the +1 convention), distinct per FormID, sane rank.
        var perks = save.PlayerPerks(_ => true);
        Assert.All(perks, p =>
        {
            Assert.True(p.RefId > 0);                                   // index + 1, never the reserved 0
            Assert.Equal(save.ResolveIref(p.RefId - 1), p.FormId);
            Assert.InRange(p.Rank, 0, 255);
        });
        Assert.Equal(perks.Select(p => p.FormId).Distinct().Count(), perks.Count);

        // Decoding perks is read-only: it must not alter the file.
        Assert.Equal(File.ReadAllBytes(path), save.ToBytes());
    }

    [Fact]
    public void Perks_is_empty_not_null_when_predicate_rejects_all()
    {
        // No FormID is a PERK by this predicate, so the list is a present, empty result (no false positives).
        var save = FalloutSave.Parse(InventorySave.Build());
        Assert.NotNull(save.PlayerPerks(_ => false));
        Assert.Empty(save.PlayerPerks(_ => false));
    }

    [Fact]
    public void q1_to_q2_is_a_real_zero_to_one_perk_diff_the_chargen_trait()
    {
        // CORRECTION (ROADMAP §4n): q2's Hoarder was previously mis-called a "havok phantom". It is the player's
        // genuinely-selected chargen *trait* (FNV stores traits as PERK forms). Proof, masters-free via a narrow
        // predicate: q1 (2.5 min, pre-trait) has the FormID nowhere and decodes 0 perks; q2 (5 min, after trait
        // selection) carries a real count-1 perk list 04 7C [Hoarder] 7C 01 7C. So q1->q2 IS a valid 0->1 pair.
        const uint hoarder = 0x0501276D;
        bool IsHoarder(uint fid) => fid == hoarder;

        var (q1, q2) = (SaveNamed("q1"), SaveNamed("q2"));
        if (q1 is null || q2 is null)
            return; // controlled pair not present on this machine — nothing to assert

        Assert.Empty(FalloutSave.Load(q1).PlayerPerks(IsHoarder));

        var trait = FalloutSave.Load(q2).PlayerPerks(IsHoarder).Single();
        Assert.Equal(hoarder, trait.FormId);
        Assert.Equal(1, trait.Rank);
    }

    [Fact]
    public void Chosen_and_engine_perks_are_unioned_across_separate_lists()
    {
        // Regression guard for the reverted single-list bug: a chosen perk (Confirmed Bachelor, in the chosen/trait
        // list) and the engine-granted Companion Suite (in its OWN separate count-prefixed list) must BOTH decode.
        // Masters-free: the predicate accepts exactly those two FormIDs, so Hoarder (also in the chosen list) is
        // filtered out — proving the reader validates list structure independently of the PERK filter.
        const uint confirmedBachelor = 0x001361B4;
        const uint companionSuite = 0x0015C571;
        bool IsEither(uint fid) => fid == confirmedBachelor || fid == companionSuite;

        var save = SaveNamed("level2-gunsbachelor");
        if (save is null)
            return; // not present on this machine

        var perks = FalloutSave.Load(save).PlayerPerks(IsEither).Select(p => p.FormId).ToHashSet();
        Assert.Contains(confirmedBachelor, perks);   // chosen perk, in the multi-entry chosen list
        Assert.Contains(companionSuite, perks);      // engine perk, in a separate count-1 list
    }
}
