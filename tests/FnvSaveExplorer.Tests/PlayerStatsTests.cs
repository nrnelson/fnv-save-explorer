using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>
/// Player karma + XP — two float32 actor-values in the player reference change form's post-MOVE array
/// (ROADMAP §4j). Located by controlled in-game diffs (vanilla Saves 33/34/35 for XP +50 each, 35/36/37
/// for karma +100 each) and confirmed on a second character; karma = slot 100, XP = slot 101.
/// </summary>
public class PlayerStatsTests
{
    [Fact]
    public void Karma_and_Xp_are_null_and_TrySet_fails_when_array_absent()
    {
        // The synthetic inventory record carries a MOVE block but only a short (non-232-slot) array, so the
        // karma/XP slot locator declines — reads are null and edits no-op, the graceful path for records
        // whose pre-list region isn't the vanilla fixed array (e.g. bit2/bit10 havok-physics records).
        var save = FalloutSave.Parse(InventorySave.Build());

        Assert.Null(save.Karma);
        Assert.Null(save.Xp);
        Assert.False(save.TrySetKarma(50f));
        Assert.False(save.TrySetXp(50f));
        Assert.False(save.HasPendingEdits);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_read_and_safely_edit_karma_and_xp(string path)
    {
        var save = FalloutSave.Load(path);

        // XP is never negative; where located, a same-length float edit must not shift the file and re-parse.
        if (save.Xp is { } xp)
        {
            Assert.True(xp >= 0f);
            Assert.True(save.TrySetXp(4242f));
            var edited = save.ToBytes();
            Assert.Equal(save.FileLength, edited.Length);
            Assert.Equal(4242f, FalloutSave.Parse(edited).Xp);
        }

        // Karma can be negative; edit round-trips same-length too (re-load to avoid mixing staged edits).
        if (save.Karma is not null)
        {
            var clean = FalloutSave.Load(path);
            Assert.True(clean.TrySetKarma(-321f));
            var edited = clean.ToBytes();
            Assert.Equal(clean.FileLength, edited.Length);
            Assert.Equal(-321f, FalloutSave.Parse(edited).Karma);
        }
    }
}
