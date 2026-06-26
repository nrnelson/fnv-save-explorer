using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class ReputationTests
{
    private static string? SaveNamed(string prefix) => FalloutSaveTests.RealSaves()
        .Select(o => (string)o[0])
        .FirstOrDefault(p => Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Controlled_diff_wiping_a_faction_zeroes_its_fame()
    {
        // rep4-pre -> rep4-post: Goodsprings (REPU 0x00104C22) was idolized (fame 100) then zeroed via the
        // console (setreputation 104c22 .. 0), removing it from the Pip-Boy. Masters-free — the predicate accepts
        // exactly the Goodsprings REPU FormID; reputation is a type-0x2B change form [fame:f32][7C][infamy:f32][7C]
        // keyed by array[refID-1] (ROADMAP §4o).
        const uint goodsprings = 0x00104C22;
        bool IsGoodsprings(uint fid) => fid == goodsprings;

        var (pre, post) = (SaveNamed("rep4-pre"), SaveNamed("rep4-post"));
        if (pre is null || post is null)
            return; // controlled pair not present on this machine

        var before = FalloutSave.Load(pre).Reputations(IsGoodsprings).Single();
        Assert.Equal(100f, before.Fame);
        Assert.Equal(0f, before.Infamy);

        var after = FalloutSave.Load(post).Reputations(IsGoodsprings).Single();
        Assert.Equal(0f, after.Fame);            // idolized -> wiped
        Assert.Equal(0f, after.Infamy);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_reputation_decode_is_well_formed_and_read_only(string path)
    {
        var save = FalloutSave.Load(path);

        // Accept-all predicate exercises the full type-0x2B scan; every hit must obey §4o: a positive refId whose
        // faction is FormIdArray[refId-1], and sane fame/infamy (0..100, the FNV reputation range), distinct per
        // faction.
        var reps = save.Reputations(_ => true);
        Assert.All(reps, r =>
        {
            Assert.True(r.RefId > 0);
            Assert.Equal(save.ResolveIref(r.RefId - 1), r.FactionFormId);
            Assert.InRange(r.Fame, 0f, 100f);
            Assert.InRange(r.Infamy, 0f, 100f);
        });
        Assert.Equal(reps.Select(r => r.FactionFormId).Distinct().Count(), reps.Count);

        // Reading reputation is read-only: it must not alter the file.
        Assert.Equal(File.ReadAllBytes(path), save.ToBytes());
    }
}
