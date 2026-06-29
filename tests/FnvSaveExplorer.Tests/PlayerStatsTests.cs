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

    [Fact]
    public void Limb_conditions_empty_when_actor_value_array_absent()
    {
        // Same graceful decline as karma/XP: the synthetic record has only a short (non-232-slot) array, so the
        // limb slots (180–185) can't be located — read is empty and edits no-op (ROADMAP §4n).
        var save = FalloutSave.Parse(InventorySave.Build());
        Assert.Empty(save.PlayerLimbConditions());
        Assert.False(save.TrySetLimbCondition("Left Leg", 0f));
        Assert.False(save.TryRepairAllLimbs());
        Assert.False(save.HasPendingEdits);
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_read_and_safely_edit_limbs(string path)
    {
        // Limb condition = six float32 at actor-value array slots 180–185 (ROADMAP §4n), located by the same
        // mechanism as karma/XP. Where located, all six read as sane condition values (0 = full, negative =
        // damage, ≈-100 = crippled) — verified across characters (Beadley/Nathan/Mace Windu); a misdecode onto
        // wrong bytes would surface as a huge/NaN float. A same-length limb edit must not shift the file.
        var save = FalloutSave.Load(path);
        var limbs = save.PlayerLimbConditions();
        if (limbs.Count == 0)
            return; // record/array not locatable (e.g. havok-physics player record) — declines like karma/XP

        Assert.Equal(6, limbs.Count);
        Assert.Equal(ReferenceChangeForm.PlayerLimbNames, limbs.Select(l => l.Name).ToArray());
        Assert.All(limbs, l => Assert.True(float.IsFinite(l.Condition) && l.Condition is >= -1000f and <= 100f,
            $"{l.Name} = {l.Condition} out of sane limb-condition range ({Path.GetFileName(path)})"));

        var clean = FalloutSave.Load(path);
        Assert.True(clean.TrySetLimbCondition("Head", -12.5f));
        var edited = clean.ToBytes();
        Assert.Equal(clean.FileLength, edited.Length);
        Assert.Equal(-12.5f, FalloutSave.Parse(edited).PlayerLimbConditions().First(l => l.Name == "Head").Condition);
    }

    [Fact]
    public void Crippled_controlled_saves_decode_known_limb_conditions()
    {
        // Ground-truth pins from the Beadley cripple/heal controlled captures (ROADMAP §4n). Each check is
        // skipped if that capture isn't on this machine, so the test is a no-op off the dev box.
        var byName = FalloutSaveTests.RealSaves()
            .Select(o => (string)o[0])
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        void Check(string save, string limb, float expected)
        {
            if (!byName.TryGetValue(save, out var path))
                return;
            var l = FalloutSave.Load(path).PlayerLimbConditions().FirstOrDefault(x => x.Name == limb);
            Assert.Equal(expected, l.Condition);
        }

        Check("crippled-both-legs", "Left Leg", -100f);
        Check("crippled-both-legs", "Right Leg", -100f);
        Check("crippled-one-leg", "Left Leg", -100f);   // right repaired, left still crippled
        Check("crippled-one-leg", "Right Leg", -58f);
        Check("crippled-zero-legs", "Left Leg", -58f);
        Check("crippled-zero-legs", "Right Leg", -58f);
        Check("beadley-leftarmcripple", "Left Arm", -100f);
        Check("beadley-armlegcripple", "Right Arm", -100f);
        Check("beadley-armlegcripple", "Left Leg", -100f);
        // Cross-character controlled confirmation: a Mace Windu left-leg cripple lands on the SAME slot (183)
        // at the SAME crippled floor (-100), proving the slot map + scale generalize (ROADMAP §4n part B).
        Check("macewindu-limbcripple-post", "Left Leg", -100f);
    }

    [Fact]
    public void Chem_controlled_saves_decode_active_av_modifier()
    {
        // Active actor-value modifiers live in the LOW region of the dense array (slot = AV index), §4n.
        // Ground truth: chempre→chempost consumes one Jet, whose masters ALCH effect is actorValue 12
        // (Action Points) magnitude 15 — so an "Action Points +15" modifier must appear only in chempost.
        var byName = FalloutSaveTests.RealSaves()
            .Select(o => (string)o[0])
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        FalloutSave.ActorValueModifier? Ap(string save) =>
            byName.TryGetValue(save, out var path)
                ? FalloutSave.Load(path).PlayerActiveEffects().Cast<FalloutSave.ActorValueModifier?>()
                    .FirstOrDefault(e => e!.Value.Slot == 12)
                : null;

        if (byName.ContainsKey("chempost"))
        {
            var post = Ap("chempost");
            Assert.NotNull(post);
            Assert.Equal("Action Points", post!.Value.Name);
            Assert.Equal(15f, post.Value.Modifier);
        }
        if (byName.ContainsKey("chempre"))
            Assert.Null(Ap("chempre")); // Jet not yet taken — no Action Points modifier
    }

    [Theory]
    [MemberData(nameof(FalloutSaveTests.RealSaves), MemberType = typeof(FalloutSaveTests))]
    public void Real_saves_active_av_modifiers_are_sane(string path)
    {
        // Where the actor-value array is locatable, every reported active modifier sits in the low region
        // (slot 0..76), is finite, and isn't absurd — a misread of havok bytes would surface as a wild/NaN
        // float, not the clean small modifiers chems/equipment produce. Read-only (no edit path to verify).
        var effects = FalloutSave.Load(path).PlayerActiveEffects();
        Assert.All(effects, e =>
        {
            Assert.InRange(e.Slot, 0, ReferenceChangeForm.ActorValueModifierSlotCount - 1);
            Assert.True(float.IsFinite(e.Modifier) && Math.Abs(e.Modifier) <= 100_000f,
                $"slot {e.Slot} modifier {e.Modifier} out of sane range ({Path.GetFileName(path)})");
            Assert.NotEqual(0f, e.Modifier); // zero slots are not reported
        });
    }
}
