using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class PlayerSpecialTests
{
    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_locate_plausible_SPECIAL(string path)
    {
        var save = FalloutSave.Load(path);

        Assert.NotNull(save.Special);
        Assert.Equal(7, save.Special!.Values.Count);
        Assert.All(save.Special.Values, v => Assert.InRange(v, (byte)1, (byte)15));
        // A legitimately-located SPECIAL sums to a sane total (chargen is 40; implants/perks push it up a bit).
        Assert.InRange(save.Special.Sum, 30, 80);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void SPECIAL_edit_is_same_length_and_reparses(string path)
    {
        var save = FalloutSave.Load(path);

        byte[] maxed = [10, 10, 10, 10, 10, 10, 10];
        Assert.True(save.TrySetSpecial(maxed));

        var edited = save.ToBytes();
        Assert.Equal(save.FileLength, edited.Length); // nothing shifted

        var reloaded = FalloutSave.Parse(edited);
        Assert.Equal(maxed, reloaded.Special!.Values);
    }
}
