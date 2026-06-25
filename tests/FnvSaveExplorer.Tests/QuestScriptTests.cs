using FnvSaveExplorer.Core;

namespace FnvSaveExplorer.Tests;

public class QuestScriptTests
{
    [Fact]
    public void Parses_real_they_went_that_a_way_stage10_script()
    {
        // The actual SCTX of "They Went That-a-Way" (VMQ01) stage 10, dumped from FalloutNV.esm. A commented-out
        // call and a reference method call (.enable) must be ignored; only the three live SetObjectiveDisplayed remain.
        const string sctx =
            "SetObjectiveDisplayed VMQ01 10 1\n" +
            ";SetObjectiveDisplayed VMQ01 20 1\n" +
            "SetObjectiveDisplayed VMQ01 25 1\n" +
            "SetObjectiveDisplayed VMQ01 30 1\n" +
            "GoodspringsCemeteryMapMarker.enable ; was disabled for intro cutscene";

        var effects = QuestScript.Parse(sctx);

        Assert.Equal(3, effects.Count);
        Assert.All(effects, e =>
        {
            Assert.Equal(QuestScriptVerb.SetObjectiveDisplayed, e.Verb);
            Assert.Equal("VMQ01", e.TargetQuestEdid);
            Assert.Equal(1, e.Arg2);          // displayed = on
            Assert.False(e.Conditional);
        });
        Assert.Equal([10, 25, 30], effects.Select(e => e.Arg1));
    }

    [Fact]
    public void Flags_calls_inside_an_if_block_as_conditional()
    {
        // VMQ01 stage with an if-guarded SetObjectiveDisplayed (the bPrimmClosed branch).
        const string sctx =
            "ShowMap NovacMapMarker\n" +
            "SetObjectiveCompleted VMQ01 20 1\n" +
            "if VMQ01.bPrimmClosed == 0\n" +
            "   SetObjectiveDisplayed VMQ01 40 1\n" +
            "endif";

        var effects = QuestScript.Parse(sctx);

        var completed = Assert.Single(effects, e => e.Verb == QuestScriptVerb.SetObjectiveCompleted);
        Assert.Equal(20, completed.Arg1);
        Assert.False(completed.Conditional);

        var displayed = Assert.Single(effects, e => e.Verb == QuestScriptVerb.SetObjectiveDisplayed);
        Assert.Equal(40, displayed.Arg1);
        Assert.True(displayed.Conditional); // inside the if-block
    }

    [Fact]
    public void Tracks_guards_when_if_elseif_have_no_space_before_paren()
    {
        // Real VGenericTimer form: `elseif(nEvent == 3)` with NO space before the paren. A token-equality keyword
        // check tokenises `elseif(nEvent` as one word and silently desyncs the guard stack, making the inner
        // SetStage look NON-conditional (which would falsely propagate in QuestPipboy). ROADMAP §6 #16.
        const string sctx =
            "if(fTimer > 0)\n" +
            "   if (nEvent == 2)\n" +
            "      SetStage VMQYesMan01 70\n" +
            "   elseif(nEvent == 3)\n" +
            "      SetStage VCG01 200\n" +
            "      SetStage VCG02 5\n" +
            "   endif\n" +
            "endif";

        var effects = QuestScript.Parse(sctx);

        // Every SetStage here is nested inside at least the `if(fTimer > 0)` guard -> all conditional.
        Assert.Equal(3, effects.Count);
        Assert.All(effects, e => Assert.True(e.Conditional));
        var vcg02 = Assert.Single(effects, e => e.TargetQuestEdid == "VCG02");
        Assert.Contains("nEvent == 3", string.Join(" | ", vcg02.Guards)); // the elseif guard was captured
    }

    [Fact]
    public void Recognises_completion_and_stage_verbs_and_ignores_others()
    {
        const string sctx =
            "; Achievement\n" +
            "AddAchievement 32\n" +
            "CompleteAllObjectives VMQ01\n" +
            "CompleteQuest VMQ01\n" +
            "SetStage VCG02 54\n" +
            "RewardXP 1000";

        var effects = QuestScript.Parse(sctx);

        Assert.Equal(3, effects.Count);
        Assert.Contains(effects, e => e.Verb == QuestScriptVerb.CompleteAllObjectives && e.TargetQuestEdid == "VMQ01");
        Assert.Contains(effects, e => e.Verb == QuestScriptVerb.CompleteQuest && e.TargetQuestEdid == "VMQ01");
        Assert.Contains(effects, e => e.Verb == QuestScriptVerb.SetStage && e.TargetQuestEdid == "VCG02" && e.Arg1 == 54);
    }

    [Fact]
    public void Defaults_objective_flag_to_on_when_omitted_and_skips_variable_args()
    {
        // "SetObjectiveDisplayed Q 5" with no flag -> on=1. "SetStage Q SomeVar" -> non-literal, skipped.
        var effects = QuestScript.Parse("SetObjectiveDisplayed Q 5\nSetStage Q SomeVar");

        var e = Assert.Single(effects);
        Assert.Equal(QuestScriptVerb.SetObjectiveDisplayed, e.Verb);
        Assert.Equal(5, e.Arg1);
        Assert.Equal(1, e.Arg2);
    }

    [Fact]
    public void Off_flag_is_preserved()
    {
        var e = Assert.Single(QuestScript.Parse("SetObjectiveDisplayed Q 7 0"));
        Assert.Equal(0, e.Arg2); // explicitly hidden
    }

    [Fact]
    public void Empty_or_null_text_yields_no_effects()
    {
        Assert.Empty(QuestScript.Parse(null));
        Assert.Empty(QuestScript.Parse("   \n ; just a comment\n"));
    }

    // ---- ROADMAP §6 #16 Stage 1: counter increments + counter-guard detection ----

    [Fact]
    public void Parses_real_powder_ganger_ondeath_counter_increment()
    {
        // The actual Goodsprings Powder Ganger OnDeath script from FalloutNV.esm — the increment that feeds
        // Ghost Town Gunfight's `nGangerDeathCount >= 6` completion guard (parenthesised, qualified RHS).
        const string sctx =
            "scn VMS16GangerScript\n" +
            "Begin OnDeath\n" +
            "   set VMS16.nGangerDeathCount to (VMS16.nGangerDeathCount + 1)\n" +
            "End";

        var inc = Assert.Single(QuestScript.ParseCounterIncrements(sctx));
        Assert.Equal("VMS16", inc.QuestEdid);
        Assert.Equal("nGangerDeathCount", inc.Counter);
        Assert.Equal(1, inc.Delta);
    }

    [Fact]
    public void Counter_increment_handles_bare_form_and_decrement_and_ignores_resets()
    {
        // Bare self form (QuestEdid empty), a decrement, and a reset that must NOT be treated as an increment.
        var bare = Assert.Single(QuestScript.ParseCounterIncrements("set nHostages to nHostages + 2"));
        Assert.Equal("", bare.QuestEdid);
        Assert.Equal("nHostages", bare.Counter);
        Assert.Equal(2, bare.Delta);

        var dec = Assert.Single(QuestScript.ParseCounterIncrements("set VFoo.nCount to VFoo.nCount - 1"));
        Assert.Equal(-1, dec.Delta);

        Assert.Empty(QuestScript.ParseCounterIncrements("set VFoo.nCount to 0"));        // reset, not an increment
        Assert.Empty(QuestScript.ParseCounterIncrements("set VFoo.nCount to OtherVar + 1")); // RHS isn't the counter
    }

    [Fact]
    public void Finds_counter_comparison_in_a_guard()
    {
        var counters = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nGangerDeathCount" };

        var hit = QuestScript.FindCounterComparison("nGangerDeathCount >= 6", counters);
        Assert.NotNull(hit);
        Assert.Equal(("nGangerDeathCount", ">=", 6), (hit.Value.Counter, hit.Value.Op, hit.Value.Threshold));

        // Reversed form flips the operator; a qualified name matches on its suffix.
        var rev = QuestScript.FindCounterComparison("6 <= VMS16.nGangerDeathCount", counters);
        Assert.NotNull(rev);
        Assert.Equal((">=", 6), (rev.Value.Op, rev.Value.Threshold));

        // A guard testing a non-counter (a do-once flag not in the set) is not matched.
        Assert.Null(QuestScript.FindCounterComparison("DoOnce == 0", counters));
    }

    [Fact]
    public void Parses_enable_ref_calls()
    {
        // The HELIOS One completion shape: a script enables FX refs by editor id (with/without an arg). A comment
        // and a non-Enable method call are ignored.
        const string sctx =
            "FXHeliosCollector1REF.Enable 1\n" +
            "FXHeliosGodRay01REF1.Enable\n" +
            "SomeMarkerREF.Disable 0   ; not an Enable\n" +
            "GSJoeCobbRef.AddScriptPackage GSPGTravelPackage";

        var refs = QuestScript.ParseEnableRefs(sctx);

        Assert.Equal(["FXHeliosCollector1REF", "FXHeliosGodRay01REF1"], refs);
    }
}
