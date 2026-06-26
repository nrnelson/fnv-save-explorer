using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PlayerSpecialTests
{
    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_locate_plausible_SPECIAL(string path)
    {
        var save = FalloutSave.Load(path);

        // SPECIAL is a change-form delta (it lives in the player-base 0x0A actor record), so a very-early save
        // taken before the player's SPECIAL is committed simply doesn't serialize it — the record is the short
        // name-only variant and Special is correctly null (e.g. the controlled save q1, where q2 a moment later
        // does carry it). Skip those, mirroring how the inventory/skills theories skip saves lacking the feature;
        // wherever SPECIAL *is* present it must be plausible.
        if (save.Special is not { } special)
            return;
        Assert.Equal(7, special.Values.Count);
        Assert.All(special.Values, v => Assert.InRange(v, (byte)1, (byte)15));
        // A legitimately-located SPECIAL sums to a sane total (chargen is 40; implants/perks push it up a bit).
        Assert.InRange(special.Sum, 30, 80);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void SPECIAL_edit_is_same_length_and_reparses(string path)
    {
        var save = FalloutSave.Load(path);

        // No serialized SPECIAL to edit (early save — see Real_saves_locate_plausible_SPECIAL); skip.
        if (save.Special is null)
            return;
        byte[] maxed = [10, 10, 10, 10, 10, 10, 10];
        Assert.True(save.TrySetSpecial(maxed));

        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length); // nothing shifted

        var reloaded = FalloutSave.Parse(edited);
        Assert.Equal(maxed, reloaded.Special!.Values);
    }
}
