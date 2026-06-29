using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

/// <summary>Pins the three-valued (Kleene) semantics of <see cref="GuardEvaluator"/> and its first decoded function
/// set (<c>GetDead</c> + quest-state queries) — ROADMAP §6 #16 Stage B. The evaluator must answer <c>true</c>/
/// <c>false</c> only when the guard is decidable from decoded state and <c>null</c> (unknown) otherwise, so a caller
/// firing only on definite-<c>true</c> can never complete a quest on a condition it couldn't actually check.</summary>
public class GuardEvaluatorTests
{
    private static readonly HashSet<uint> None = [];

    // A quest "VFSAtomicPimp" (0x1000) with one boss ref; objectives all start not-completed/not-displayed.
    private static GuardEvaluator Build(
        IReadOnlySet<uint> dead,
        IReadOnlyDictionary<string, uint>? refEdids = null,
        Func<uint, bool?>? completed = null,
        Func<uint, int, bool?>? objCompleted = null,
        Func<string, int?>? resolveVar = null)
    {
        refEdids ??= new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { ["VFSFistoREF"] = 0x55 };
        var byEdid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase) { ["VFSAtomicPimp"] = 0x1000, ["OtherQuest"] = 0x2000 };
        return new GuardEvaluator(
            dead, refEdids,
            e => byEdid.TryGetValue(e, out var f) ? f : null,
            _ => true,                                    // running
            completed ?? (_ => false),                    // completed
            _ => null,                                    // stage unknown
            objCompleted ?? ((_, _) => false),            // objective completed
            (_, _) => false,                              // objective displayed
            resolveVar);                                  // local script-var name -> value (§6 #3)
    }

    // A resolver bound to a quest with two persisted script vars (the SLSD/SCVR-named values from the save).
    private static readonly Func<string, int?> Vars = nm =>
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["bPlayerRentedRoom"] = 1, ["nKills"] = 3 }
            .TryGetValue(nm, out var v) ? v : (int?)null;

    [Fact]
    public void Blank_guard_holds()
    {
        Assert.True(Build(None).Holds(null));
        Assert.True(Build(None).Holds("   "));
    }

    [Fact]
    public void GetDead_resolves_from_the_death_registry()
    {
        // The boss ref 0x55 is dead -> Ref.GetDead is true; alive -> false.
        Assert.True(Build(new HashSet<uint> { 0x55 }).Holds("VFSFistoREF.GetDead"));
        Assert.False(Build(None).Holds("VFSFistoREF.GetDead"));
    }

    [Fact]
    public void Unresolvable_ref_is_unknown_not_false()
    {
        // A ref the masters scan never named cannot be checked -> unknown (so a caller won't fire on it).
        Assert.Null(Build(None, refEdids: new Dictionary<string, uint>()).Holds("VFSFistoREF.GetDead"));
    }

    [Fact]
    public void Local_vars_and_unmodelled_functions_are_unknown()
    {
        Assert.Null(Build(None).Holds("doonce == 0"));               // a quest-local var we don't decode
        Assert.Null(Build(None).Holds("GetReputationThreshold >= 2"));
        Assert.Null(Build(None).Holds("SomeRef.GetUnconscious == 1"));
    }

    [Fact]
    public void Wang_dang_objective_guard_fires_only_when_the_boss_is_dead()
    {
        // The real VFSAtomicPimp obj-14 guard: completed==0 AND boss dead AND obj-15 not completed.
        const string guard = "getobjectivecompleted VFSAtomicPimp 14 == 0 && VFSFistoREF.Getdead && getobjectivecompleted VFSAtomicPimp 15 == 0";
        Assert.True(Build(new HashSet<uint> { 0x55 }).Holds(guard));   // boss dead -> fire
        Assert.False(Build(None).Holds(guard));                          // boss alive -> blocked
    }

    [Fact]
    public void Kleene_and_short_circuits_false_but_propagates_unknown()
    {
        // false && unknown == false (a dead-but-blocked branch); true && unknown == unknown.
        Assert.False(Build(None).Holds("VFSFistoREF.GetDead && doonce == 0"));            // alive(false) && unknown => false
        Assert.Null(Build(new HashSet<uint> { 0x55 }).Holds("VFSFistoREF.GetDead && doonce == 0")); // true && unknown => unknown
    }

    [Fact]
    public void Kleene_or_short_circuits_true_but_propagates_unknown()
    {
        Assert.True(Build(new HashSet<uint> { 0x55 }).Holds("VFSFistoREF.GetDead || doonce == 1"));  // true || unknown => true
        Assert.Null(Build(None).Holds("VFSFistoREF.GetDead || doonce == 1"));                          // false || unknown => unknown
    }

    [Fact]
    public void GetQuestCompleted_reads_the_computed_model()
    {
        bool? Completed(uint f) => f == 0x2000 ? true : false;
        Assert.True(Build(None, completed: Completed).Holds("GetQuestCompleted OtherQuest == 1"));
        Assert.False(Build(None, completed: Completed).Holds("GetQuestCompleted VFSAtomicPimp == 1"));
    }

    [Fact]
    public void Unknown_quest_edid_is_unknown()
    {
        Assert.Null(Build(None).Holds("GetQuestCompleted NoSuchQuest == 1"));
    }

    // ----- §6 #3: named script vars resolve against the save's persisted QuestScriptVars -----

    [Fact]
    public void Named_var_guard_evaluates_against_persisted_value()
    {
        var g = Build(None, resolveVar: Vars);
        Assert.True(g.Holds("bPlayerRentedRoom == 1"));   // persisted value 1
        Assert.False(g.Holds("bPlayerRentedRoom == 0"));
        Assert.True(g.Holds("nKills >= 3"));              // counter comparison
        Assert.False(g.Holds("nKills >= 5"));
        Assert.True(g.Holds("bPlayerRentedRoom"));        // bare truthiness (1 != 0)
    }

    [Fact]
    public void Unresolved_var_stays_unknown_even_with_a_resolver()
    {
        // A name the quest's var table doesn't carry (a global / other-quest var) must stay unknown, not false —
        // the precision-first guarantee holds for vars too.
        Assert.Null(Build(None, resolveVar: Vars).Holds("bSomeOtherFlag == 1"));
    }

    [Fact]
    public void Without_a_resolver_named_vars_remain_unknown()
    {
        // Backward-compatible: no resolver supplied -> a bare var is still unknown (precision-first default).
        Assert.Null(Build(None).Holds("bPlayerRentedRoom == 1"));
    }

    [Fact]
    public void Named_var_combines_with_GetDead_under_kleene_logic()
    {
        // boss dead AND room rented -> fire; boss alive -> blocked (false dominates AND).
        const string guard = "VFSFistoREF.GetDead && bPlayerRentedRoom == 1";
        Assert.True(Build(new HashSet<uint> { 0x55 }, resolveVar: Vars).Holds(guard));
        Assert.False(Build(None, resolveVar: Vars).Holds(guard));
    }
}
